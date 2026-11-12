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
  private FakeGluetun? _gluetun;

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
    Anonymous = _app.CreateClient();
    Client = _app.CreateClient();
    Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestEnvironment.Secret);
  }

  protected T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

  protected async Task<FakeGluetun> WithGluetunAsync(string? apiKey = null) {
    _gluetun = apiKey is null ? await FakeGluetun.StartAsync() : await FakeGluetun.StartAsync(apiKey);
    Options.Gluetun = new GluetunOptions(_gluetun.Url, "gluetun-key");
    return _gluetun;
  }

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
    if (_gluetun is not null) await _gluetun.DisposeAsync();
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
