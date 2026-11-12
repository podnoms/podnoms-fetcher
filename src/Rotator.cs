using System.Net.Http.Headers;
using System.Text.Json;

namespace PodNoms.Fetcher;

/// <summary>
/// Changes our public IP by having gluetun reconnect, which picks a new
/// server from its configured list. Only one rotation happens at a time, and
/// each one is numbered (its generation): a job refused on an IP we've since
/// moved off just tries again, rather than asking for another rotation.
/// </summary>
public sealed class Rotator(FetcherOptions options, ILogger<Rotator> logger) : IDisposable {
  private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
  private readonly Lock _state = new();
  private int _generation;
  private DateTimeOffset _lastRotatedAt = DateTimeOffset.MinValue;
  private Task<bool>? _inFlight;

  public int Generation {
    get {
      lock (_state) return _generation;
    }
  }

  private async Task<HttpResponseMessage> GluetunAsync(HttpMethod method, string path, string? json = null) {
    var (url, apiKey) = options.Gluetun!;
    using var request = new HttpRequestMessage(method, url + path);
    if (apiKey is not null) request.Headers.Add("X-API-Key", apiKey);
    if (json is not null) request.Content = new StringContent(json, new MediaTypeHeaderValue("application/json"));
    var response = await _http.SendAsync(request);
    if (response.IsSuccessStatusCode) return response;
    response.Dispose();
    throw new HttpRequestException($"gluetun answered {(int)response.StatusCode} to {method} {path}");
  }

  /// <summary>Our public IP as gluetun sees it, or null if it isn't known (yet).</summary>
  public async Task<string?> PublicIpAsync() {
    if (options.Gluetun is null) return null;
    try {
      using var response = await GluetunAsync(HttpMethod.Get, "/v1/publicip/ip");
      using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
      return body.RootElement.TryGetProperty("public_ip", out var ip) && ip.GetString() is { Length: > 0 } value ? value : null;
    } catch {
      return null;
    }
  }

  private async Task SetStatusAsync(string status) {
    using var response = await GluetunAsync(HttpMethod.Put, "/v1/vpn/status", $$"""{"status":"{{status}}"}""");
  }

  private async Task<bool> ReconnectAsync() {
    var before = await PublicIpAsync();
    await SetStatusAsync("stopped");
    await SetStatusAsync("running");
    var deadline = DateTimeOffset.UtcNow + options.RotateTimeout;
    while (DateTimeOffset.UtcNow < deadline) {
      await Task.Delay(options.RotatePoll);
      var after = await PublicIpAsync();
      if (after is null || after == before) continue;
      logger.LogInformation("Changed public IP from {before} to {after}", before, after);
      return true;
    }
    logger.LogWarning("Gluetun reconnected but the public IP did not change from {before}", before);
    return false;
  }

  private async Task<bool> RotateAsync() {
    var changed = false;
    try {
      changed = await ReconnectAsync();
    } catch (Exception error) {
      logger.LogError(error, "Could not change public IP");
    }
    lock (_state) {
      _lastRotatedAt = DateTimeOffset.UtcNow;
      if (changed) _generation++;
      _inFlight = null;
    }
    return changed;
  }

  /// <summary>
  /// For a job that was refused while <paramref name="seen"/> was the generation:
  /// whether it's worth trying again on a new IP. Rotates unless that's already
  /// happened since, or happened too recently to try again.
  /// </summary>
  public Task<bool> RotateFromAsync(int seen) {
    if (options.Gluetun is null) return Task.FromResult(false);
    lock (_state) {
      if (_generation != seen) return Task.FromResult(true);
      if (_inFlight is not null) return _inFlight;
      if (DateTimeOffset.UtcNow - _lastRotatedAt < options.RotateMinInterval) return Task.FromResult(false);
      // On another thread, so it can't finish (and clear itself) before it's stored.
      return _inFlight = Task.Run(RotateAsync);
    }
  }

  public void Dispose() => _http.Dispose();
}
