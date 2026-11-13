using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace PodNoms.Fetcher.Tests;

/// <summary>
/// A VPN for tests: a folder of WireGuard configs, one per server, for the
/// fake-vpn helper to "connect" to, and the service that tells the fetcher its
/// public IP (whatever fake-vpn last wrote to FAKE_IP_FILE).
/// </summary>
public sealed class FakeVpn : IAsyncDisposable {
  private static string IpFile => Environment.GetEnvironmentVariable("FAKE_IP_FILE")!;
  private readonly WebApplication _echo;
  private readonly string _dir = Directory.CreateTempSubdirectory("vpn-").FullName;
  private int _connectionsAtStart;

  /// <summary>What to set as <see cref="FetcherOptions.Vpn"/>.</summary>
  public VpnOptions Options { get; }

  /// <summary>How many times the fetcher has changed server since <see cref="Started"/>.</summary>
  public int Switches => Connections - _connectionsAtStart;

  private int Connections => File.Exists(Log) ? File.ReadAllLines(Log).Length : 0;
  private string Log => Path.Join(_dir, "connections.log");

  private FakeVpn(string[] ips) {
    // One server per address; servers sharing an address share a public IP.
    foreach (var (ip, index) in ips.Select((ip, index) => (ip, index)))
      File.WriteAllText(Path.Join(_dir, $"server-{index + 1}.conf"), Config($"key-{index + 1}", ip));
    Environment.SetEnvironmentVariable("FAKE_VPN_LOG", Log);
    File.Delete(IpFile);

    var builder = WebApplication.CreateSlimBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    _echo = builder.Build();
    _echo.MapGet("/", () => File.Exists(IpFile) && File.ReadAllText(IpFile) is { Length: > 0 } ip ? Results.Text(ip) : Results.StatusCode(503));
    _echo.StartAsync().GetAwaiter().GetResult();
    Options = new VpnOptions { Provider = "files", ConfigDir = _dir, Helper = TestEnvironment.Fake("fake-vpn"), PublicIpUrl = _echo.Urls.Single() };
  }

  public static FakeVpn WithServers(params string[] ips) => new(ips);

  /// <summary>Call once the fetcher has connected to its first server.</summary>
  public void Started() => _connectionsAtStart = Connections;

  public static string Config(string publicKey, string ip) => $"""
      [Interface]
      # Our key and address, as the provider's download has them.
      PrivateKey = private-{publicKey}
      Address = 10.2.0.2/32, fd00::2/128
      DNS = 10.2.0.1

      [Peer]
      PublicKey = {publicKey}
      AllowedIPs = 0.0.0.0/0, ::/0
      Endpoint = {ip}:51820
      """;

  public async ValueTask DisposeAsync() {
    await _echo.DisposeAsync();
    Environment.SetEnvironmentVariable("FAKE_VPN_LOG", null);
    File.Delete(IpFile);
    Directory.Delete(_dir, recursive: true);
  }
}
