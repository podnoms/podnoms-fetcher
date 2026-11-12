using System.Globalization;

namespace PodNoms.Fetcher;

public sealed record GluetunOptions(string Url, string? ApiKey);

/// <summary>Settings, from the environment, read once. Tests change them in place.</summary>
public sealed class FetcherOptions {
  /// <summary>Shared with PodNoms (its FETCHER_SECRET); every request must carry it.</summary>
  public required string Secret { get; set; }
  public int Port { get; set; } = 8080;
  public string YtDlpPath { get; set; } = "yt-dlp";

  /// <summary>Where downloads are kept until PodNoms collects them.</summary>
  public string WorkDir { get; set; } = "/tmp/podnoms-fetcher";

  /// <summary>Gluetun's control server. Without it, a refusal is passed straight back to PodNoms.</summary>
  public GluetunOptions? Gluetun { get; set; }

  /// <summary>How many new IPs one job may try before giving up.</summary>
  public int RotateRetries { get; set; } = 2;

  /// <summary>A refusal this soon after the last rotation isn't worth another.</summary>
  public TimeSpan RotateMinInterval { get; set; } = TimeSpan.FromSeconds(120);

  /// <summary>How long to wait for gluetun to come back with a new IP.</summary>
  public TimeSpan RotateTimeout { get; set; } = TimeSpan.FromSeconds(90);

  /// <summary>How often to ask gluetun whether the new IP is up.</summary>
  public TimeSpan RotatePoll { get; set; } = TimeSpan.FromSeconds(2);

  /// <summary>
  /// pip in yt-dlp's virtualenv (set in the Docker image). With it, yt-dlp is
  /// upgraded every <see cref="YtDlpUpdateInterval"/> while running, besides on start.
  /// </summary>
  public string? YtDlpPip { get; set; }

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

    var gluetunUrl = Text("GLUETUN_CONTROL_URL");
    var options = new FetcherOptions {
      Secret = secret,
      Port = Whole("PORT", 8080, 1, "a port number"),
      YtDlpPath = Text("YTDLP_PATH") ?? "yt-dlp",
      WorkDir = Path.GetFullPath(Text("WORK_DIR") ?? "/tmp/podnoms-fetcher"),
      Gluetun = gluetunUrl is null ? null : new GluetunOptions(gluetunUrl.TrimEnd('/'), Text("GLUETUN_API_KEY")),
      RotateRetries = Whole("ROTATE_RETRIES", 2, 0, "a whole number, 0 or more"),
      RotateMinInterval = TimeSpan.FromSeconds(Number("ROTATE_MIN_INTERVAL_SECONDS", 120, value => value >= 0, "0 or more")),
      RotateTimeout = TimeSpan.FromSeconds(Number("ROTATE_TIMEOUT_SECONDS", 90, value => value > 0, "more than 0")),
      YtDlpPip = Text("YTDLP_PIP"),
      YtDlpUpdateInterval = TimeSpan.FromHours(Number("YTDLP_UPDATE_INTERVAL_HOURS", 4, value => value >= 0, "0 or more")),
    };
    if (problems.Count > 0) throw new InvalidOperationException(string.Join("; ", problems));
    return options;
  }
}
