using System.Diagnostics;
using System.Text.Json;

namespace PodNoms.Fetcher;

// What a job answers with (see Api.cs), named as they're written to PodNoms.
public abstract record Message;

public sealed record LineMessage(string Line) : Message;

public sealed record EndMessage : Message {
  public bool End => true;
}

public sealed record FileMessage(DownloadedFile File) : Message;

public sealed record DownloadedFile(string Id, string Ext);

public sealed record ErrorMessage(string Error, bool RateLimited) : Message;

public delegate ValueTask Send(Message message);

/// <summary>
/// A listing or download, tried again on a new IP when the platform refuses
/// us. Each job's answer is a stream of messages (see Api.cs).
/// </summary>
public sealed class Jobs(FetcherOptions options, YtDlp ytDlp, Rotator rotator, YtDlpUpdater updater, DownloadStore store, ILogger<Jobs> logger) {
  /// <summary>
  /// Runs yt-dlp until it succeeds, or fails for any reason but a refusal we
  /// can get round with a new IP. Returns whether it succeeded; a failure has
  /// already been sent.
  /// </summary>
  private async Task<bool> WithRotationAsync(string kind, string url, string[] args, Send send, CancellationToken cancel, Action? beforeRetry = null) {
    for (var attempt = 0; ; attempt++) {
      var seen = rotator.Generation;
      var result = await ytDlp.RunAsync(args, line => send(new LineMessage(line)), cancel);
      if (result.Ok) return true;
      var retry = result.RateLimited && !cancel.IsCancellationRequested && attempt < options.RotateRetries && await rotator.RotateFromAsync(seen);
      if (!retry) {
        if (result.RateLimited)
          logger.LogWarning("Refused by the platform: {kind} of {url}, attempt {attempt}: {error}", kind, url, attempt, result.Error);
        await send(new ErrorMessage(result.Error, result.RateLimited));
        return false;
      }
      logger.LogInformation("Refused by the platform; trying {kind} of {url} again on a new IP after attempt {attempt}", kind, url, attempt);
      beforeRetry?.Invoke();
    }
  }

  public Task ListAsync(string url, int? limit, Send send, CancellationToken cancel) =>
      updater.AsJob(async () => {
        if (await WithRotationAsync("list", url, YtDlp.ListArgs(url, limit), send, cancel)) await send(new EndMessage());
      });

  public Task DownloadAsync(string url, string? limitRate, Send send, CancellationToken cancel) =>
      updater.AsJob(() => RunDownloadAsync(url, limitRate, send, cancel));

  private async Task RunDownloadAsync(string url, string? limitRate, Send send, CancellationToken cancel) {
    var id = Guid.NewGuid().ToString();
    var dir = store.EnsureWorkDir();
    string? filepath = null;
    ValueTask Watch(Message message) {
      if (message is LineMessage { Line: var line } && line.StartsWith("INFO ", StringComparison.Ordinal)) filepath = SavedTo(line[5..]) ?? filepath;
      return send(message);
    }

    var started = Stopwatch.GetTimestamp();
    var args = YtDlp.DownloadArgs(url, store.OutputTemplate(id), limitRate);
    if (!await WithRotationAsync("download", url, args, Watch, cancel, () => store.RemoveJobFiles(id))) {
      store.RemoveJobFiles(id);
      return;
    }

    var ext = filepath is null ? "" : Path.GetExtension(filepath).TrimStart('.');
    // Only a file of this job's, in the work folder.
    if (filepath is null || ext is "" || Path.GetFullPath(filepath) != Path.Join(dir, $"{id}.{ext}")) {
      store.RemoveJobFiles(id);
      await send(new ErrorMessage("yt-dlp didn't say where it saved the download", false));
      return;
    }
    store.Keep(id, Path.GetFullPath(filepath));
    logger.LogInformation("Downloaded {url} as {id} in {durationMs}ms", url, id, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    await send(new FileMessage(new DownloadedFile(id, ext)));
  }

  // The filepath in the JSON of yt-dlp's INFO line.
  private static string? SavedTo(string json) {
    try {
      using var info = JsonDocument.Parse(json);
      return info.RootElement.ValueKind == JsonValueKind.Object
          && info.RootElement.TryGetProperty("filepath", out var filepath)
          && filepath.ValueKind == JsonValueKind.String
          ? filepath.GetString()
          : null;
    } catch (JsonException) {
      return null;
    }
  }
}
