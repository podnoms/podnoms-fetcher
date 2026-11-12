using System.Collections.Concurrent;

namespace PodNoms.Fetcher;

/// <summary>
/// Finished downloads, kept until PodNoms collects (and deletes) them, and
/// swept away if it never does, along with anything left over from failed runs.
/// </summary>
public sealed class DownloadStore(FetcherOptions options, ILogger<DownloadStore> logger) {
  private readonly ConcurrentDictionary<string, string> _files = new();

  public string EnsureWorkDir() => Directory.CreateDirectory(options.WorkDir).FullName;

  /// <summary>Every file a download writes starts with its id.</summary>
  public string OutputTemplate(string id) => Path.Join(options.WorkDir, $"{id}.%(ext)s");

  public void Keep(string id, string path) => _files[id] = path;

  public string? PathOf(string id) => _files.GetValueOrDefault(id);

  public bool Remove(string id) {
    if (!_files.TryRemove(id, out var path)) return false;
    Delete(new FileInfo(path));
    return true;
  }

  private IEnumerable<FileSystemInfo> Entries() {
    var dir = new DirectoryInfo(options.WorkDir);
    return dir.Exists ? dir.EnumerateFileSystemInfos() : [];
  }

  /// <summary>Removes the rest of a job's files (partial downloads and the like).</summary>
  public void RemoveJobFiles(string id) {
    foreach (var entry in Entries().Where(entry => entry.Name.StartsWith($"{id}.", StringComparison.Ordinal)))
      Delete(entry);
  }

  public void Sweep() => Sweep(DateTimeOffset.UtcNow);

  public void Sweep(DateTimeOffset now) {
    foreach (var entry in Entries()) {
      if (now - entry.LastWriteTimeUtc < options.FileTtl) continue;
      Delete(entry);
      _files.TryRemove(KeyValuePair.Create(entry.Name.Split('.')[0], entry.FullName));
      logger.LogInformation("Removed an uncollected download, {file}", entry.Name);
    }
  }

  private static void Delete(FileSystemInfo entry) {
    try {
      if (entry is DirectoryInfo dir) dir.Delete(recursive: true);
      else entry.Delete();
    } catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) {
      // Already gone.
    }
  }
}

/// <summary>Sweeps the downloads every ten minutes.</summary>
public sealed class DownloadSweeper(DownloadStore store, ILogger<DownloadSweeper> logger) : BackgroundService {
  protected override async Task ExecuteAsync(CancellationToken stopping) {
    using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
    while (await timer.WaitForNextTickAsync(stopping)) {
      try {
        store.Sweep();
      } catch (Exception error) {
        logger.LogError(error, "Could not sweep downloads");
      }
    }
  }
}
