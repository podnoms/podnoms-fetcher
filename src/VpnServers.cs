using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace PodNoms.Fetcher;

/// <summary>
/// A WireGuard server we can leave by, and what we need to be to it: providers
/// differ in whether our key and address are the same for every server.
/// </summary>
public sealed record VpnServer(string Name, string PublicKey, string Endpoint, string PrivateKey, string Address, string Dns);

/// <summary>Where the servers come from: a folder of WireGuard configs, Mullvad's list, or the list of Proton's we carry.</summary>
public static class VpnServers {
  public const string MullvadRelays = "https://api.mullvad.net/www/relays/wireguard/";

  // Mullvad's resolver inside the tunnel, and the port its servers listen on.
  private const string MullvadDns = "10.64.0.1";
  private const int MullvadPort = 51820;

  // What Proton gives every WireGuard client: its address and resolver in the
  // tunnel, and the port its servers listen on.
  private const string ProtonAddress = "10.2.0.2/32";
  private const string ProtonDns = "10.2.0.1";
  private const int ProtonPort = 51820;

  public static async Task<IReadOnlyList<VpnServer>> LoadAsync(VpnOptions vpn, HttpClient http) => vpn.Provider switch {
    "mullvad" => FromMullvad(await http.GetStringAsync(MullvadRelays), vpn),
    "protonvpn" => FromProton(ProtonServers(), vpn),
    _ => FromConfigs(vpn.ConfigDir),
  };

  /// <summary>The list built into the fetcher (src/ProtonVpnServers.json).</summary>
  public static string ProtonServers() {
    using var list = typeof(VpnServers).Assembly.GetManifestResourceStream("ProtonVpnServers.json")!;
    using var reader = new StreamReader(list);
    return reader.ReadToEnd();
  }

  /// <summary>
  /// Proton's servers in the countries asked for (by name; all of them if
  /// none). One key serves them all. Proton doesn't publish its list, so this
  /// is one we carry, and some of it will have gone: a server that doesn't
  /// come up is passed over.
  /// </summary>
  public static IReadOnlyList<VpnServer> FromProton(string json, VpnOptions vpn) {
    using var list = JsonDocument.Parse(json);
    var servers = new List<VpnServer>();
    foreach (var server in list.RootElement.GetProperty("servers").EnumerateArray()) {
      var country = server.GetProperty("country").GetString()!;
      if (vpn.Countries.Count > 0 && !vpn.Countries.Contains(country, StringComparer.OrdinalIgnoreCase)) continue;
      servers.Add(new VpnServer(
          server.GetProperty("name").GetString()!,
          server.GetProperty("publicKey").GetString()!,
          $"{server.GetProperty("ip").GetString()}:{ProtonPort}",
          vpn.PrivateKey!,
          vpn.Address ?? ProtonAddress,
          ProtonDns));
    }
    return servers;
  }

  /// <summary>Every *.conf in the folder that's a usable WireGuard config, named for its file.</summary>
  public static IReadOnlyList<VpnServer> FromConfigs(string dir) =>
      !Directory.Exists(dir)
          ? []
          : [.. Directory.EnumerateFiles(dir, "*.conf").Order(StringComparer.Ordinal)
              .Select(path => ParseConfig(Path.GetFileNameWithoutExtension(path), File.ReadAllText(path)))
              .OfType<VpnServer>()];

  /// <summary>
  /// A wg-quick config, as providers hand them out: our key, address and
  /// resolver under [Interface], the server under [Peer]. Null if anything
  /// needed is missing.
  /// </summary>
  public static VpnServer? ParseConfig(string name, string text) {
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var section = "";
    foreach (var raw in text.Split('\n')) {
      var line = raw.Split('#')[0].Trim();
      if (line.StartsWith('[')) section = line.Trim('[', ']').Trim();
      else if (line.Split('=', 2) is [var key, var value]) values.TryAdd($"{section}.{key.Trim()}", value.Trim());
    }

    // An address or resolver may be a list, IPv6 among them; the tunnel is IPv4.
    string? FirstIpv4(string key) =>
        values.GetValueOrDefault(key)?.Split(',').Select(item => item.Trim())
            .FirstOrDefault(item => IPAddress.TryParse(item.Split('/')[0], out var ip) && ip.AddressFamily == AddressFamily.InterNetwork);

    return values.GetValueOrDefault("Interface.PrivateKey") is { Length: > 0 } privateKey
        && FirstIpv4("Interface.Address") is { } address
        && FirstIpv4("Interface.DNS") is { } dns
        && values.GetValueOrDefault("Peer.PublicKey") is { Length: > 0 } publicKey
        && Endpoint(values.GetValueOrDefault("Peer.Endpoint")) is { } endpoint
            ? new VpnServer(name, publicKey, endpoint, privateKey, address, dns)
            : null;
  }

  // As ip:port. A name is looked up now, while names can still be looked up
  // outside the tunnel.
  private static string? Endpoint(string? endpoint) {
    if (endpoint?.LastIndexOf(':') is not (> 0 and var colon) || !int.TryParse(endpoint[(colon + 1)..], out var port)) return null;
    var host = endpoint[..colon];
    if (IPAddress.TryParse(host, out var ip)) return ip.AddressFamily == AddressFamily.InterNetwork ? $"{ip}:{port}" : null;
    try {
      return Dns.GetHostAddresses(host, AddressFamily.InterNetwork).FirstOrDefault() is { } found ? $"{found}:{port}" : null;
    } catch (SocketException) {
      return null;
    }
  }

  /// <summary>
  /// Mullvad's active servers in the countries asked for (by code or name; all
  /// of them if none), from its relay list. One key and address serve them all.
  /// </summary>
  public static IReadOnlyList<VpnServer> FromMullvad(string json, VpnOptions vpn) {
    using var relays = JsonDocument.Parse(json);
    var servers = new List<VpnServer>();
    foreach (var relay in relays.RootElement.EnumerateArray()) {
      string? Text(string name) => relay.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
      if (!relay.TryGetProperty("active", out var active) || active.ValueKind != JsonValueKind.True) continue;
      if (Text("hostname") is not { } hostname || Text("pubkey") is not { } publicKey || Text("ipv4_addr_in") is not { } ip) continue;
      var wanted = vpn.Countries.Count == 0 || vpn.Countries.Any(country =>
          country.Equals(Text("country_code"), StringComparison.OrdinalIgnoreCase) || country.Equals(Text("country_name"), StringComparison.OrdinalIgnoreCase));
      if (wanted) servers.Add(new VpnServer(hostname, publicKey, $"{ip}:{MullvadPort}", vpn.PrivateKey!, vpn.Address!, MullvadDns));
    }
    return servers;
  }
}
