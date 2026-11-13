using System.Runtime.CompilerServices;

// The fetcher's state and the fakes' settings (environment variables) are
// shared, so one test at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace PodNoms.Fetcher.Tests;

/// <summary>The environment the fetcher starts in, set before any test runs.</summary>
public static class TestEnvironment {
  public const string Secret = "test-secret-0123456789";

  /// <summary>A folder of this run's own, for work folders and the fakes' files.</summary>
  public static readonly string Dir = Directory.CreateTempSubdirectory("podnoms-fetcher-test-").FullName;

  public static string Fake(string name) => Path.Join(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? $"{name}.exe" : name);

  [ModuleInitializer]
  public static void Set() {
    Environment.SetEnvironmentVariable("FETCHER_SECRET", Secret);
    Environment.SetEnvironmentVariable("YTDLP_PATH", Fake("fake-yt-dlp"));
    Environment.SetEnvironmentVariable("WORK_DIR", Path.Join(Dir, "work"));
    Environment.SetEnvironmentVariable("FAKE_IP_FILE", Path.Join(Dir, "ip"));
  }
}
