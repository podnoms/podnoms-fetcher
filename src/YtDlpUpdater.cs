namespace PodNoms.Fetcher;

public abstract record UpdateResult;

/// <summary>Why an update didn't run: "not configured" or "jobs running".</summary>
public sealed record UpdateSkipped(string Reason) : UpdateResult;

public sealed record UpdateRan(string? From, string? To, bool Updated) : UpdateResult;

/// <summary>
/// Keeps yt-dlp up to date while the fetcher runs (the entrypoint updates it on
/// start too), as YouTube changes often and an old yt-dlp is refused sooner.
/// The same as PodNoms' ytdlp-update job: an update only starts while no job
/// is running, and no job starts until it's done, so yt-dlp's files are never
/// replaced under a running yt-dlp.
/// </summary>
public sealed class YtDlpUpdater(FetcherOptions options, YtDlp ytDlp, ILogger<YtDlpUpdater> logger) {
  private static readonly string[] PipArgs =
      ["install", "--no-cache-dir", "--quiet", "--disable-pip-version-check", "--upgrade", "yt-dlp[default]"];

  private readonly Lock _state = new();
  private int _active;
  private Task? _updating;

  /// <summary>Wraps a job: waits out an update in progress, and counts it as running.</summary>
  public async Task<T> AsJob<T>(Func<Task<T>> work) {
    while (true) {
      Task? updating;
      lock (_state) {
        updating = _updating;
        if (updating is null) {
          _active++;
          break;
        }
      }
      await updating;
    }
    try {
      return await work();
    } finally {
      lock (_state) _active--;
    }
  }

  public Task AsJob(Func<Task> work) =>
      AsJob(async () => {
        await work();
        return true;
      });

  /// <summary>Upgrades yt-dlp, unless jobs are running. Throws if pip fails.</summary>
  public async Task<UpdateResult> UpdateAsync() {
    if (options.YtDlpPip is not { } pip) return new UpdateSkipped("not configured");
    // Completed whether the update works or not; it only holds jobs back.
    var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    lock (_state) {
      if (_active > 0 || _updating is not null) return new UpdateSkipped("jobs running");
      _updating = done.Task;
    }
    try {
      var from = await ytDlp.VersionAsync();
      var (exitCode, _, errors) = await Subprocess.RunAsync(pip, PipArgs, TimeSpan.FromMinutes(5));
      if (exitCode != 0) throw new InvalidOperationException($"pip exited with code {exitCode}: {errors.Trim()}");
      ytDlp.ForgetVersion();
      var to = await ytDlp.VersionAsync();
      if (from != to) logger.LogInformation("Updated yt-dlp from {from} to {to}", from, to);
      return new UpdateRan(from, to, from != to);
    } finally {
      lock (_state) _updating = null;
      done.SetResult();
    }
  }
}

/// <summary>
/// Updates yt-dlp every YTDLP_UPDATE_INTERVAL_HOURS, retrying every 10 minutes
/// while jobs keep it from running.
/// </summary>
public sealed class YtDlpUpdateSchedule(FetcherOptions options, YtDlpUpdater updater, ILogger<YtDlpUpdateSchedule> logger) : BackgroundService {
  private static readonly TimeSpan Retry = TimeSpan.FromMinutes(10);

  protected override async Task ExecuteAsync(CancellationToken stopping) {
    var interval = options.YtDlpUpdateInterval;
    if (options.YtDlpPip is null || interval <= TimeSpan.Zero) return;
    var wait = interval;
    while (true) {
      await Task.Delay(wait, stopping);
      wait = interval;
      try {
        if (await updater.UpdateAsync() is UpdateSkipped) wait = Retry;
      } catch (Exception error) {
        logger.LogWarning(error, "Could not update yt-dlp");
      }
    }
  }
}
