using System.Net;

namespace PodNoms.Fetcher;

/// <summary>
/// The WireGuard tunnel everything we send goes through, when one is set up
/// (VPN_PROVIDER). The fetcher chooses the server; docker/fetcher-vpn, run as
/// root, does the networking and is the kill switch.
/// </summary>
public sealed class Vpn(FetcherOptions options, ILogger<Vpn> logger) : IDisposable {
  private static readonly TimeSpan ServerListAge = TimeSpan.FromHours(24);
  private const int StartAttempts = 10;

  // A new connection each time: one made through the last server is dead.
  private readonly HttpClient _http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.Zero }) { Timeout = TimeSpan.FromSeconds(5) };
  private readonly Dictionary<string, DateTimeOffset> _lastUsed = [];
  private IReadOnlyList<VpnServer> _servers = [];
  private DateTimeOffset _loadedAt;

  public bool Enabled => options.Vpn is not null;

  /// <summary>The server we're on, and our address as the world sees it; null if that isn't known.</summary>
  public VpnServer? Server { get; private set; }
  public string? PublicIp { get; private set; }

  /// <summary>Brings the tunnel up on the first server that works. Throws if none does.</summary>
  public async Task StartAsync() {
    var vpn = options.Vpn ?? throw new InvalidOperationException("No VPN is set up");
    // Before the tunnel (and its firewall) is up, so Mullvad's list can be fetched.
    _servers = await VpnServers.LoadAsync(vpn, _http);
    _loadedAt = DateTimeOffset.UtcNow;
    if (_servers.Count == 0) {
      throw new InvalidOperationException(vpn.Provider == "files"
          ? $"No usable WireGuard configs (*.conf) in {vpn.ConfigDir}"
          : $"{vpn.Provider} has no servers in SERVER_COUNTRIES ({string.Join(", ", vpn.Countries)})");
    }
    // Some of a list may have gone, but not this many in a row: something else is wrong.
    var tried = (await CandidatesAsync()).Take(StartAttempts).ToList();
    foreach (var server in tried) {
      if (await ConnectAsync(server) is not null) return;
    }
    throw new InvalidOperationException($"None of the {tried.Count} VPN servers tried (of {_servers.Count}) came up");
  }

  /// <summary>The other servers, those we've been on longest ago (or never) first, in no order among equals.</summary>
  public async Task<IReadOnlyList<VpnServer>> CandidatesAsync() {
    await RefreshAsync();
    return [.. _servers.Where(server => server != Server)
        .OrderBy(server => _lastUsed.GetValueOrDefault(server.Name))
        .ThenBy(_ => Random.Shared.Next())];
  }

  // Mullvad's servers come and go; a folder's are read again too, so configs can be added.
  private async Task RefreshAsync() {
    if (options.Vpn is not { } vpn || DateTimeOffset.UtcNow - _loadedAt < ServerListAge) return;
    try {
      if (await VpnServers.LoadAsync(vpn, _http) is { Count: > 0 } servers) _servers = servers;
    } catch (Exception error) {
      logger.LogWarning(error, "Could not refresh the VPN servers; keeping the {count} known", _servers.Count);
    }
    _loadedAt = DateTimeOffset.UtcNow;
  }

  /// <summary>
  /// Moves the tunnel to this server and waits for it to work. Returns our new
  /// public IP, or null if the server didn't come up in time. Throws if the
  /// tunnel couldn't be changed at all, which no other server would fix.
  /// </summary>
  public async Task<string?> ConnectAsync(VpnServer server) {
    var vpn = options.Vpn!;
    Server = server;
    PublicIp = null;
    _lastUsed[server.Name] = DateTimeOffset.UtcNow;

    // Handed over in a file only we (and root) can read, not on the command line.
    var keyFile = Path.Join(Path.GetTempPath(), $"fetcher-vpn-{Guid.NewGuid():N}");
    try {
      var create = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
      if (!OperatingSystem.IsWindows()) create.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
      await using (var file = new FileStream(keyFile, create))
      await using (var writer = new StreamWriter(file)) {
        await writer.WriteLineAsync(server.PrivateKey);
      }
      string[] args = ["connect", keyFile, server.Address, server.Dns, server.PublicKey, server.Endpoint, options.Port.ToString()];
      var (exitCode, _, errors) = await Subprocess.RunAsync(vpn.Helper, args, TimeSpan.FromSeconds(30));
      if (exitCode != 0) throw new InvalidOperationException($"Could not change the VPN tunnel: {(errors.Trim() is { Length: > 0 } said ? said : $"{vpn.Helper} exited with code {exitCode}")}");
    } finally {
      File.Delete(keyFile);
    }

    // Nothing gets out except through the tunnel, so an answer means it's up.
    var deadline = DateTimeOffset.UtcNow + options.VpnConnectTimeout;
    while (true) {
      if (await AskPublicIpAsync(vpn.PublicIpUrl) is { } ip) {
        logger.LogInformation("Connected the VPN to {server}, public IP {publicIp}", server.Name, ip);
        return PublicIp = ip;
      }
      if (DateTimeOffset.UtcNow >= deadline) break;
      await Task.Delay(options.RotatePoll);
    }
    logger.LogWarning("The VPN server {server} did not come up", server.Name);
    return null;
  }

  private async Task<string?> AskPublicIpAsync(string url) {
    try {
      var answer = (await _http.GetStringAsync(url)).Trim();
      return IPAddress.TryParse(answer, out _) ? answer : null;
    } catch (Exception error) when (error is HttpRequestException or TaskCanceledException) {
      return null;
    }
  }

  public void Dispose() => _http.Dispose();
}
