namespace PodNoms.Fetcher;

/// <summary>
/// Changes our public IP by moving the VPN to another server. Only one
/// rotation happens at a time, and each one is numbered (its generation): a
/// job refused on an IP we've since moved off just tries again, rather than
/// asking for another rotation.
/// </summary>
public sealed class Rotator(FetcherOptions options, Vpn vpn, ILogger<Rotator> logger) {
  private readonly Lock _state = new();
  private int _generation;
  private DateTimeOffset _lastRotatedAt = DateTimeOffset.MinValue;
  private Task<bool>? _inFlight;

  public int Generation {
    get {
      lock (_state) return _generation;
    }
  }

  // Tries the servers we've been off longest until one gives us an IP other
  // than the one refused, for as long as ROTATE_TIMEOUT_SECONDS allows.
  private async Task<bool> ReconnectAsync() {
    var before = vpn.PublicIp;
    var deadline = DateTimeOffset.UtcNow + options.RotateTimeout;
    foreach (var server in await vpn.CandidatesAsync()) {
      if (DateTimeOffset.UtcNow >= deadline) break;
      var after = await vpn.ConnectAsync(server);
      if (after is null) continue;
      if (after != before) {
        logger.LogInformation("Changed public IP from {before} to {after}", before, after);
        return true;
      }
      logger.LogInformation("The VPN server {server} has the public IP we were refused on, {before}", server.Name, before);
    }
    logger.LogWarning("No VPN server gave us a public IP other than {before}", before);
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
    if (!vpn.Enabled) return Task.FromResult(false);
    lock (_state) {
      if (_generation != seen) return Task.FromResult(true);
      if (_inFlight is not null) return _inFlight;
      if (DateTimeOffset.UtcNow - _lastRotatedAt < options.RotateMinInterval) return Task.FromResult(false);
      // On another thread, so it can't finish (and clear itself) before it's stored.
      return _inFlight = Task.Run(RotateAsync);
    }
  }
}
