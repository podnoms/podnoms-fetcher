using Microsoft.Extensions.Logging.Console;
using PodNoms.Fetcher;

var builder = WebApplication.CreateSlimBuilder(args);

FetcherOptions options;
try {
  options = FetcherOptions.Load(builder.Configuration);
} catch (InvalidOperationException error) {
  Console.Error.WriteLine(error.Message);
  return 1;
}

builder.Logging.ClearProviders();
builder.Logging.AddConsole(console => console.FormatterName = JsonLineFormatter.FormatterName);
builder.Services.AddSingleton<ConsoleFormatter, JsonLineFormatter>();
// Ours, and anything that goes wrong in the framework.
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);

builder.WebHost.UseUrls($"http://*:{options.Port}");

// State is in memory: one of each, and a single instance of the fetcher.
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<YtDlp>();
builder.Services.AddSingleton<Vpn>();
builder.Services.AddSingleton<Rotator>();
builder.Services.AddSingleton<DownloadStore>();
builder.Services.AddSingleton<YtDlpUpdater>();
builder.Services.AddSingleton<Jobs>();
builder.Services.AddHostedService<DownloadSweeper>();
builder.Services.AddHostedService<YtDlpUpdateSchedule>();

var app = builder.Build();
app.UseFetcherApi();

app.Services.GetRequiredService<DownloadStore>().EnsureWorkDir();

// The tunnel first: nothing is sent, and no job taken, without it.
var vpn = app.Services.GetRequiredService<Vpn>();
if (vpn.Enabled) {
  try {
    await vpn.StartAsync();
  } catch (Exception error) {
    app.Logger.LogError(error, "Could not start the VPN");
    // Disposing writes out the log, which is otherwise lost as we exit.
    await app.DisposeAsync();
    return 1;
  }
}

// YouTube changes often, and an old yt-dlp is refused sooner. A failed update
// isn't fatal; the version in the image is used.
var ytDlp = app.Services.GetRequiredService<YtDlp>();
if (options.YtDlpUpdateOnStart) {
  try {
    await app.Services.GetRequiredService<YtDlpUpdater>().UpdateAsync();
  } catch (Exception error) {
    app.Logger.LogWarning(error, "Could not update yt-dlp; using {ytDlpVersion}", await ytDlp.VersionAsync());
  }
}

await app.StartAsync();
app.Logger.LogInformation(
    "Listening on port {port} with yt-dlp {ytDlpVersion}, public IP {publicIp}, VPN server {vpnServer}",
    options.Port,
    await ytDlp.VersionAsync(),
    vpn.PublicIp,
    vpn.Server?.Name);
await app.WaitForShutdownAsync();
return 0;
