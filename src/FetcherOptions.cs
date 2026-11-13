using System.Globalization;

namespace PodNoms.Fetcher;

/// <summary>The VPN the fetcher runs itself (see Vpn.cs).</summary>
public sealed record VpnOptions {
  /// <summary>"files" (a folder of WireGuard configs, one per server), "protonvpn" (the list we carry) or "mullvad" (its own list).</summary>
  public required string Provider { get; init; }
  public string ConfigDir { get; init; } = "/etc/fetcher/wireguard";

  /// <summary>For Proton and Mullvad: our key and (for Mullvad) address, the same on every server.</summary>
  public string? PrivateKey { get; init; }
  public string? Address { get; init; }

  /// <summary>For Proton and Mullvad: the countries to use, by name (or code, for Mullvad). All of them if empty.</summary>
  public IReadOnlyList<string> Countries { get; init; } = [];

  /// <summary>Answers with the address it sees us at, as plain text.</summary>
  public string PublicIpUrl { get; init; } = "https://api.ipify.org";

  /// <summary>docker/fetcher-vpn in the image.</summary>
  public string Helper { get; init; } = "fetcher-vpn";
}

/// <summary>Settings, from the environment, read once. Tests change them in place.</summary>
public sealed class FetcherOptions {
  /// <summary>Shared with PodNoms (its FETCHER_SECRET); every request must carry it.</summary>
  public required string Secret { get; set; }
  public int Port { get; set; } = 8080;
  public string YtDlpPath { get; set; } = "yt-dlp";

  /// <summary>Where downloads are kept until PodNoms collects them.</summary>
  public string WorkDir { get; set; } = "/tmp/podnoms-fetcher";

  /// <summary>Without one, a refusal is passed straight back to PodNoms.</summary>
  public VpnOptions? Vpn { get; set; }

  /// <summary>How long a VPN server gets to come up before the next is tried.</summary>
  public TimeSpan VpnConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);

  /// <summary>How many new IPs one job may try before giving up.</summary>
  public int RotateRetries { get; set; } = 2;

  /// <summary>A refusal this soon after the last rotation isn't worth another.</summary>
  public TimeSpan RotateMinInterval { get; set; } = TimeSpan.FromSeconds(120);

  /// <summary>How long to go on trying servers for a new IP.</summary>
  public TimeSpan RotateTimeout { get; set; } = TimeSpan.FromSeconds(90);

  /// <summary>How often to ask whether the new server is up.</summary>
  public TimeSpan RotatePoll { get; set; } = TimeSpan.FromSeconds(2);

  /// <summary>
  /// pip in yt-dlp's virtualenv (set in the Docker image). With it, yt-dlp is
  /// upgraded every <see cref="YtDlpUpdateInterval"/> while running, besides on start.
  /// </summary>
  public string? YtDlpPip { get; set; }

  /// <summary>Whether to upgrade yt-dlp before taking jobs (set in the Docker image).</summary>
  public bool YtDlpUpdateOnStart { get; set; }

  /// <summary>Zero turns the updates off.</summary>
  public TimeSpan YtDlpUpdateInterval { get; set; } = TimeSpan.FromHours(4);

  /// <summary>Downloads nobody has collected are removed after this.</summary>
  public TimeSpan FileTtl { get; set; } = TimeSpan.FromHours(1);

  /// <summary>Reads the settings, throwing with every problem found if any are wrong.</summary>
  public static FetcherOptions Load(IConfiguration configuration) {
    var problems = new List<string>();

    // An empty variable counts as unset.
    string? Text(string name) => configuration[name] is { Length: > 0 } value ? value : null;

    double Number(string name, double fallback, Func<double, bool> valid, string expected) {
      if (Text(name) is not { } text) return fallback;
      if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && valid(value))
        return value;
      problems.Add($"{name} must be {expected}");
      return fallback;
    }

    int Whole(string name, int fallback, int min, string expected) =>
        (int)Number(name, fallback, value => value >= min && value <= int.MaxValue && value == Math.Floor(value), expected);

    var secret = Text("FETCHER_SECRET") ?? "";
    if (secret.Length < 16) problems.Add("FETCHER_SECRET must be at least 16 characters");

    VpnOptions? vpn = null;
    if (Text("VPN_PROVIDER")?.ToLowerInvariant() is { } provider) {
      var defaults = new VpnOptions { Provider = provider };
      vpn = defaults with {
        ConfigDir = Text("WIREGUARD_CONFIG_DIR") ?? defaults.ConfigDir,
        PrivateKey = Text("WIREGUARD_PRIVATE_KEY"),
        // Mullvad gives an IPv6 address too; the tunnel is IPv4.
        Address = Text("WIREGUARD_ADDRESSES")?.Split(',').Select(address => address.Trim()).FirstOrDefault(address => !address.Contains(':')),
        Countries = [.. (Text("SERVER_COUNTRIES") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
        PublicIpUrl = Text("PUBLIC_IP_URL") ?? defaults.PublicIpUrl,
        Helper = Text("VPN_HELPER") ?? defaults.Helper,
      };
      if (provider is not ("files" or "protonvpn" or "mullvad")) problems.Add("VPN_PROVIDER must be files, protonvpn or mullvad");
      if (provider == "protonvpn" && vpn.PrivateKey is null) problems.Add("VPN_PROVIDER=protonvpn needs WIREGUARD_PRIVATE_KEY");
      if (provider == "mullvad" && (vpn.PrivateKey is null || vpn.Address is null))
        problems.Add("VPN_PROVIDER=mullvad needs WIREGUARD_PRIVATE_KEY and WIREGUARD_ADDRESSES");
    }

    var options = new FetcherOptions {
      Secret = secret,
      Port = Whole("PORT", 8080, 1, "a port number"),
      YtDlpPath = Text("YTDLP_PATH") ?? "yt-dlp",
      WorkDir = Path.GetFullPath(Text("WORK_DIR") ?? "/tmp/podnoms-fetcher"),
      Vpn = vpn,
      RotateRetries = Whole("ROTATE_RETRIES", 2, 0, "a whole number, 0 or more"),
      RotateMinInterval = TimeSpan.FromSeconds(Number("ROTATE_MIN_INTERVAL_SECONDS", 120, value => value >= 0, "0 or more")),
      RotateTimeout = TimeSpan.FromSeconds(Number("ROTATE_TIMEOUT_SECONDS", 90, value => value > 0, "more than 0")),
      YtDlpPip = Text("YTDLP_PIP"),
      YtDlpUpdateOnStart = Text("YTDLP_UPDATE_ON_START")?.ToLowerInvariant() is "true" or "1",
      YtDlpUpdateInterval = TimeSpan.FromHours(Number("YTDLP_UPDATE_INTERVAL_HOURS", 4, value => value >= 0, "0 or more")),
    };
    if (problems.Count > 0) throw new InvalidOperationException(string.Join("; ", problems));
    return options;
  }
}
