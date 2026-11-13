using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace PodNoms.Fetcher;

public sealed record RunResult(bool Ok, string Error = "", bool RateLimited = false);

/// <summary>
/// The yt-dlp runs PodNoms asks for, with the same arguments and the same
/// printed lines (DETAILS, PROGRESS, INFO, ENTRY, PLAYLIST) PodNoms runs locally
/// without a fetcher, so its parsing is shared. Requests never carry arguments
/// of their own.
/// </summary>
public sealed partial class YtDlp(FetcherOptions options) {
  private Task<string?>? _version;

  private static IEnumerable<string> PoliteArgs(string? limitRate = null) =>
  [
      // One extraction makes several requests; pause between them.
      "--sleep-requests", "1",
        // Fewer retries than the default 10, each waiting longer than the last.
        "--retries", "3",
        "--retry-sleep", "exp=1:60",
        .. limitRate is null ? [] : new[] { "--limit-rate", limitRate },
    ];

  /// <summary>As episode-processor.server.ts in PodNoms.</summary>
  public static string[] DownloadArgs(string url, string outputTemplate, string? limitRate) =>
  [
      .. PoliteArgs(limitRate),
        "--no-playlist",
        "--progress",
        "--newline",
        "--progress-template",
        "download:PROGRESS %(progress.{status,downloaded_bytes,total_bytes,total_bytes_estimate,speed,eta})j",
        "--no-simulate",
        "--format",
        "bestaudio/best",
        "--output",
        outputTemplate,
        "--print",
        "video:DETAILS %(.{title,description,duration,thumbnail,timestamp,upload_date})j",
        "--print",
        "after_move:INFO %(.{title,description,duration,thumbnail,filepath,timestamp,upload_date})j",
        url,
    ];

  /// <summary>As channel-listing.server.ts in PodNoms.</summary>
  public static string[] ListArgs(string url, int? limit) =>
  [
      .. PoliteArgs(),
        "--flat-playlist",
        "--lazy-playlist",
        .. limit is { } end ? new[] { "--playlist-end", end.ToString() } : [],
        "--print",
        "ENTRY %(.{id,url,webpage_url,title,duration,live_status})j",
        "--print",
        "playlist:PLAYLIST %(.{title,description,thumbnails})j",
        url,
    ];

  // The platform refusing us, rather than something wrong with the one video.
  [GeneratedRegex(@"HTTP Error 429|Too Many Requests|confirm you.?re not a bot|rate[- ]?limit", RegexOptions.IgnoreCase)]
  private static partial Regex RateLimitedPattern();

  public static bool IsRateLimited(string message) => RateLimitedPattern().IsMatch(message);

  [GeneratedRegex(@"^ERROR:\s*(\[[^\]]+\]\s*[^:\s]+:\s*)?")]
  private static partial Regex ErrorPrefix();

  /// <summary>"ERROR: [youtube] abc123: This video is unavailable" → "This video is unavailable"</summary>
  public static string ErrorMessage(string line) => ErrorPrefix().Replace(line, "", 1);

  private static RunResult Fail(string error) => new(false, error, IsRateLimited(error));

  /// <summary>
  /// Runs yt-dlp, handing each line of output (on either stream, errors aside)
  /// to <paramref name="onLine"/>, one at a time. Stopped, along with anything
  /// it started (ffmpeg), if cancelled, e.g. when PodNoms hangs up.
  /// </summary>
  public async Task<RunResult> RunAsync(IEnumerable<string> args, Func<string, ValueTask> onLine, CancellationToken cancel) {
    if (cancel.IsCancellationRequested) return Fail("Cancelled");
    Process process;
    try {
      process = Subprocess.Start(options.YtDlpPath, args);
    } catch (Exception error) {
      return Fail($"Could not run yt-dlp: {error.Message}");
    }

    using (process) {
      string? lastError = null;
      using var turn = new SemaphoreSlim(1, 1);
      async Task ReadLines(StreamReader reader) {
        while (await reader.ReadLineAsync() is { } line) {
          await turn.WaitAsync();
          try {
            if (line.StartsWith("ERROR", StringComparison.Ordinal)) lastError = ErrorMessage(line);
            else await onLine(line);
          } finally {
            turn.Release();
          }
        }
      }

      await using (cancel.Register(() => Subprocess.Kill(process))) {
        try {
          await Task.WhenAll(ReadLines(process.StandardOutput), ReadLines(process.StandardError));
          await process.WaitForExitAsync(CancellationToken.None);
        } finally {
          Subprocess.Kill(process);
        }
      }

      if (process.ExitCode == 0) return new RunResult(true);
      if (cancel.IsCancellationRequested) return Fail("Cancelled");
      return Fail(string.IsNullOrEmpty(lastError) ? $"yt-dlp exited with code {process.ExitCode}" : lastError);
    }
  }

  /// <summary>After an update, so the next <see cref="VersionAsync"/> asks yt-dlp again.</summary>
  public void ForgetVersion() => _version = null;

  /// <summary>yt-dlp's version, or null if it can't be run.</summary>
  public Task<string?> VersionAsync() => _version ??= AskVersionAsync();

  private async Task<string?> AskVersionAsync() {
    try {
      var (exitCode, output, _) = await Subprocess.RunAsync(options.YtDlpPath, ["--version"], TimeSpan.FromMinutes(1));
      return exitCode == 0 ? output.Trim() : null;
    } catch {
      return null;
    }
  }
}

/// <summary>Child processes, with no shell and no stdin.</summary>
public static class Subprocess {
  public static Process Start(string path, IEnumerable<string> args) {
    var start = new ProcessStartInfo(path) {
      UseShellExecute = false,
      RedirectStandardInput = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      StandardOutputEncoding = Encoding.UTF8,
      StandardErrorEncoding = Encoding.UTF8,
    };
    foreach (var arg in args) start.ArgumentList.Add(arg);
    var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {path}");
    process.StandardInput.Close();
    return process;
  }

  /// <summary>Stops the process and everything it started, if it's still running.</summary>
  public static void Kill(Process process) {
    try {
      if (!process.HasExited) process.Kill(entireProcessTree: true);
    } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException) {
      // Already gone.
    }
  }

  /// <summary>Runs to the end, collecting the output. Throws if it can't start or runs past the timeout.</summary>
  public static async Task<(int ExitCode, string Output, string Errors)> RunAsync(string path, IEnumerable<string> args, TimeSpan timeout) {
    using var process = Start(path, args);
    using var timedOut = new CancellationTokenSource(timeout);
    try {
      var output = process.StandardOutput.ReadToEndAsync(timedOut.Token);
      var errors = process.StandardError.ReadToEndAsync(timedOut.Token);
      await process.WaitForExitAsync(timedOut.Token);
      return (process.ExitCode, await output, await errors);
    } catch (OperationCanceledException) {
      throw new TimeoutException($"{Path.GetFileName(path)} took longer than {timeout.TotalSeconds:0}s");
    } finally {
      Kill(process);
    }
  }
}
