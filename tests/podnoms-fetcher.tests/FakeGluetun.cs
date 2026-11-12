using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace PodNoms.Fetcher.Tests;

/// <summary>
/// Stands in for gluetun's control server. Each reconnect (stopped, then
/// running) moves to the next IP, which it writes to FAKE_IP_FILE for the fake
/// yt-dlp to read.
/// </summary>
public sealed class FakeGluetun : IAsyncDisposable {
  private static readonly string[] Ips = ["10.0.0.1", "10.0.0.2", "10.0.0.3", "10.0.0.4"];
  private readonly WebApplication _app;
  private readonly Lock _state = new();
  private int _index;
  private bool _running = true;

  public string ApiKey { get; }
  public string Url => _app.Urls.Single();
  public int Stops { get; private set; }

  private FakeGluetun(string apiKey) {
    ApiKey = apiKey;
    var builder = WebApplication.CreateSlimBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    _app = builder.Build();
    _app.Use(async (context, next) => {
      if (context.Request.Headers["X-API-Key"] == apiKey) await next(context);
      else context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    });
    _app.MapGet("/v1/publicip/ip", () => {
      lock (_state) return Results.Json(new { public_ip = Ip() });
    });
    _app.MapPut("/v1/vpn/status", (StatusChange change) => {
      lock (_state) {
        if (change.Status == "stopped") {
          _running = false;
          Stops++;
        } else {
          _running = true;
          _index++;
        }
        WriteIp();
      }
      return Results.Json(new { outcome = change.Status });
    });
    WriteIp();
  }

  private sealed record StatusChange(string Status);

  private string Ip() => _running ? Ips[Math.Min(_index, Ips.Length - 1)] : "";

  private void WriteIp() => File.WriteAllText(Environment.GetEnvironmentVariable("FAKE_IP_FILE")!, Ip());

  public static async Task<FakeGluetun> StartAsync(string apiKey = "gluetun-key") {
    var gluetun = new FakeGluetun(apiKey);
    await gluetun._app.StartAsync();
    return gluetun;
  }

  public async ValueTask DisposeAsync() {
    await _app.DisposeAsync();
    File.Delete(Environment.GetEnvironmentVariable("FAKE_IP_FILE")!);
  }
}
