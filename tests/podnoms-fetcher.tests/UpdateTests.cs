namespace PodNoms.Fetcher.Tests;

public sealed class UpdateTests : FetcherTest, IDisposable {
  private static readonly string[] FakePipSettings = ["FAKE_PIP_VERSION_FILE", "FAKE_PIP_NEW_VERSION", "FAKE_PIP_FAIL", "FAKE_PIP_HOLD_MS"];
  private readonly YtDlpUpdater _updater;

  public UpdateTests() {
    Options.YtDlpPip = TestEnvironment.Fake("fake-pip");
    Environment.SetEnvironmentVariable("FAKE_PIP_VERSION_FILE", Path.Join(Options.WorkDir, "version"));
    Service<YtDlp>().ForgetVersion();
    _updater = Service<YtDlpUpdater>();
  }

  public void Dispose() {
    foreach (var name in FakePipSettings) Environment.SetEnvironmentVariable(name, null);
  }

  [Fact]
  public async Task Does_nothing_without_YTDLP_PIP() {
    Options.YtDlpPip = null;
    Assert.Equal(new UpdateSkipped("not configured"), await _updater.UpdateAsync());
  }

  [Fact]
  public async Task Upgrades_yt_dlp_and_reports_the_versions() {
    Environment.SetEnvironmentVariable("FAKE_PIP_NEW_VERSION", "2026.11.08");
    Assert.Equal(new UpdateRan("2026.08.19", "2026.11.08", true), await _updater.UpdateAsync());
    Assert.Contains("\"ytDlpVersion\":\"2026.11.08\"", await Client.GetStringAsync("/health"));
  }

  [Fact]
  public async Task Says_so_when_it_was_already_the_latest() {
    Assert.Equal(new UpdateRan("2026.08.19", "2026.08.19", false), await _updater.UpdateAsync());
  }

  [Fact]
  public async Task Waits_for_another_time_while_a_job_is_running() {
    // A job that runs until told to finish.
    var finish = new TaskCompletionSource();
    var job = _updater.AsJob(() => finish.Task);
    Assert.Equal(new UpdateSkipped("jobs running"), await _updater.UpdateAsync());
    finish.SetResult();
    await job;
  }

  [Fact]
  public async Task Holds_new_jobs_until_its_done() {
    Environment.SetEnvironmentVariable("FAKE_PIP_HOLD_MS", "300");
    var update = _updater.UpdateAsync();
    var started = false;
    var job = _updater.AsJob(() => {
      started = true;
      return Task.CompletedTask;
    });
    await Task.Delay(50);
    Assert.False(started);
    await update;
    await job;
    Assert.True(started);
  }

  [Fact]
  public async Task Fails_when_pip_does_letting_jobs_run_again() {
    Environment.SetEnvironmentVariable("FAKE_PIP_FAIL", "1");
    await Assert.ThrowsAsync<InvalidOperationException>(() => _updater.UpdateAsync());
    Assert.Equal("ran", await _updater.AsJob(() => Task.FromResult("ran")));
  }
}
