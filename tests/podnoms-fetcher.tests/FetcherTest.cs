using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace PodNoms.Fetcher.Tests;

/// <summary>
/// Each test gets a fetcher of its own, with its own work folder and no
/// rotation set up, and calls it as PodNoms does.
/// </summary>
public abstract class FetcherTest : IAsyncLifetime {
  private readonly WebApplicationFactory<Program> _app = new();
  private FakeVpn? _vpn;

  protected FetcherOptions Options { get; }

  /// <summary>Sends the secret; <see cref="Anonymous"/> doesn't.</summary>
  protected HttpClient Client { get; }
  protected HttpClient Anonymous { get; }

  protected FetcherTest() {
    Options = Service<FetcherOptions>();
    Options.WorkDir = Directory.CreateTempSubdirectory("work-").FullName;
    Options.RotateMinInterval = TimeSpan.Zero;
    Options.RotatePoll = TimeSpan.FromMilliseconds(5);
    Options.RotateTimeout = TimeSpan.FromSeconds(2);
    Options.VpnConnectTimeout = TimeSpan.FromMilliseconds(300);
    Anonymous = _app.CreateClient();
    Client = _app.CreateClient();
    Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestEnvironment.Secret);
  }

  protected T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

  /// <summary>Gives the fetcher a VPN with a server at each of these addresses, connected to one of them.</summary>
  protected async Task<FakeVpn> WithVpnAsync(params string[] ips) {
    _vpn = FakeVpn.WithServers(ips);
    Options.Vpn = _vpn.Options;
    await Vpn.StartAsync();
    _vpn.Started();
    return _vpn;
  }

  protected Vpn Vpn => Service<Vpn>();

  protected Task<HttpResponseMessage> PostAsync(string path, object body) => Client.PostAsJsonAsync(path, body);

  /// <summary>Runs a job and returns the messages it answered with, as they were written.</summary>
  protected async Task<string[]> JobAsync(string path, object body) {
    using var response = await PostAsync(path, body);
    Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.ToString());
    return (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
  }

  protected string[] WorkDirFiles() => Directory.GetFileSystemEntries(Options.WorkDir);

  public Task InitializeAsync() => Task.CompletedTask;

  public async Task DisposeAsync() {
    if (_vpn is not null) await _vpn.DisposeAsync();
    await _app.DisposeAsync();
    Directory.Delete(Options.WorkDir, recursive: true);
  }
}

public static class Messages {
  /// <summary>The yt-dlp output lines among a job's messages.</summary>
  public static string[] Lines(this string[] messages) =>
      [.. messages.Select(Parse).Where(message => message.TryGetProperty("line", out _)).Select(message => message.GetProperty("line").GetString()!)];

  /// <summary>The download a job ended with.</summary>
  public static (string Id, string Ext) File(this string[] messages) {
    var last = Parse(messages[^1]);
    Assert.True(last.TryGetProperty("file", out var file), $"Ended with {messages[^1]}");
    return (file.GetProperty("id").GetString()!, file.GetProperty("ext").GetString()!);
  }

  public static bool EndedRateLimited(this string[] messages) =>
      Parse(messages[^1]).TryGetProperty("rateLimited", out var rateLimited) && rateLimited.GetBoolean();

  private static System.Text.Json.JsonElement Parse(string message) => System.Text.Json.JsonDocument.Parse(message).RootElement;
}
