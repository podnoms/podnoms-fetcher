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
builder.Services.AddSingleton<Rotator>();
builder.Services.AddSingleton<DownloadStore>();
builder.Services.AddSingleton<YtDlpUpdater>();
builder.Services.AddSingleton<Jobs>();
builder.Services.AddHostedService<DownloadSweeper>();
builder.Services.AddHostedService<YtDlpUpdateSchedule>();

var app = builder.Build();
app.UseFetcherApi();

app.Services.GetRequiredService<DownloadStore>().EnsureWorkDir();
await app.StartAsync();
app.Logger.LogInformation(
    "Listening on port {port} with yt-dlp {ytDlpVersion}, public IP {publicIp}, rotation {rotation}",
    options.Port,
    await app.Services.GetRequiredService<YtDlp>().VersionAsync(),
    await app.Services.GetRequiredService<Rotator>().PublicIpAsync(),
    options.Gluetun is not null);
await app.WaitForShutdownAsync();
return 0;
