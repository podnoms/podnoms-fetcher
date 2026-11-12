using System.Net;
using System.Net.Http.Headers;

namespace PodNoms.Fetcher.Tests;

public sealed class ApiTests : FetcherTest {
  [Fact]
  public async Task Turns_away_requests_without_the_secret() {
    Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous.GetAsync("/health")).StatusCode);
    Anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-secret-0123456");
    Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous.GetAsync("/health")).StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous.GetAsync("/no-such-route")).StatusCode);
  }

  [Fact]
  public async Task Reports_its_health() {
    Assert.Equal("""{"ytDlpVersion":"2026.08.19","publicIp":null}""", await Client.GetStringAsync("/health"));
  }

  [Fact]
  public async Task Streams_a_channels_entries_then_ends() {
    var result = await JobAsync("/list", new { url = "https://video.test/channel?entries=c,b,a", limit = 2 });
    Assert.Equal(2, result.Lines().Count(line => line.StartsWith("ENTRY ")));
    Assert.Contains(result.Lines(), line => line.StartsWith("PLAYLIST "));
    Assert.Equal("""{"end":true}""", result[^1]);
  }

  [Fact]
  public async Task Lists_everything_without_a_limit() {
    var result = await JobAsync("/list", new { url = "https://video.test/channel?entries=c,b,a", limit = (int?)null });
    Assert.Equal(3, result.Lines().Count(line => line.StartsWith("ENTRY ")));
  }

  [Fact]
  public async Task Rejects_anything_but_a_link_and_a_limit() {
    var bad = await PostAsync("/list", new { url = "file:///etc/passwd", limit = 1 });
    Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    Assert.Equal("""{"error":"Expected { url, limit }"}""", await bad.Content.ReadAsStringAsync());
    Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/list", new { url = "https://video.test/x" })).StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/list", new { url = "https://video.test/x", limit = 0 })).StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/list", new { url = "--exec", limit = 1 })).StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, (await Client.PostAsync("/list", new StringContent("not json"))).StatusCode);
  }

  [Fact]
  public async Task Passes_on_failures() {
    var result = await JobAsync("/list", new { url = "https://video.test/fail", limit = (int?)null });
    Assert.Equal("""{"error":"This video is unavailable","rateLimited":false}""", result[^1]);
  }

  [Fact]
  public async Task Streams_progress_then_the_file_to_collect_which_can_be_deleted() {
    var result = await JobAsync("/download", new { url = "https://video.test/watch" });
    Assert.Equal(3, result.Lines().Count(line => line.StartsWith("PROGRESS ")));
    Assert.Contains(result.Lines(), line => line.StartsWith("DETAILS "));
    var (id, ext) = result.File();
    Assert.Equal("m4a", ext);
    Assert.Equal("{\"file\":{\"id\":\"" + id + "\",\"ext\":\"m4a\"}}", result[^1]);

    using var file = await Client.GetAsync($"/files/{id}");
    Assert.Equal(HttpStatusCode.OK, file.StatusCode);
    Assert.Equal("application/octet-stream", file.Content.Headers.ContentType?.ToString());
    Assert.Equal(10, file.Content.Headers.ContentLength);
    Assert.Equal("fake audio", await file.Content.ReadAsStringAsync());

    Assert.Equal(HttpStatusCode.NoContent, (await Client.DeleteAsync($"/files/{id}")).StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/files/{id}")).StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, (await Client.DeleteAsync($"/files/{id}")).StatusCode);
    Assert.Empty(WorkDirFiles());
  }

  [Fact]
  public async Task Fails_if_yt_dlp_doesnt_say_where_it_saved_the_file_leaving_nothing_behind() {
    var result = await JobAsync("/download", new { url = "https://video.test/no-info" });
    Assert.Equal("""{"error":"yt-dlp didn't say where it saved the download","rateLimited":false}""", result[^1]);
    Assert.Empty(WorkDirFiles());
  }

  [Fact]
  public async Task Only_takes_yt_dlp_rate_limits() {
    var bad = await PostAsync("/download", new { url = "https://video.test/watch", limitRate = "--exec rm" });
    Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/download", new { url = "https://video.test/watch", limitRate = "2M\n" })).StatusCode);
    Assert.Equal("m4a", (await JobAsync("/download", new { url = "https://video.test/watch", limitRate = "2.5M" })).File().Ext);
  }

  [Fact]
  public async Task Wont_serve_files_it_didnt_download() {
    var response = await Client.GetAsync("/files/..%2F..%2Fetc%2Fpasswd");
    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    Assert.Equal("""{"error":"No such file"}""", await response.Content.ReadAsStringAsync());
    Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/files/{Guid.NewGuid()}")).StatusCode);
  }

  [Fact]
  public async Task Says_so_when_refused_without_gluetun_to_change_ip() {
    Assert.True((await JobAsync("/download", new { url = "https://video.test/rate-limited" })).EndedRateLimited());
  }

  [Fact]
  public async Task Changes_ip_and_tries_again() {
    var gluetun = await WithGluetunAsync();
    var result = await JobAsync("/download", new { url = "https://video.test/watch?refuse=10.0.0.1" });
    Assert.Equal("m4a", result.File().Ext);
    Assert.Equal(1, gluetun.Stops);
    Assert.Contains("\"publicIp\":\"10.0.0.2\"", await Client.GetStringAsync("/health"));
  }

  [Fact]
  public async Task Gives_up_after_its_retries() {
    var gluetun = await WithGluetunAsync();
    Assert.True((await JobAsync("/list", new { url = "https://video.test/rate-limited", limit = 1 })).EndedRateLimited());
    Assert.Equal(Options.RotateRetries, gluetun.Stops);
  }

  [Fact]
  public async Task Changes_ip_once_for_refusals_that_come_together() {
    var gluetun = await WithGluetunAsync();
    var body = new { url = "https://video.test/watch?refuse=10.0.0.1" };
    var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => JobAsync("/download", body)));
    Assert.All(results, result => Assert.Equal("m4a", result.File().Ext));
    Assert.Equal(1, gluetun.Stops);
  }

  [Fact]
  public async Task Doesnt_change_ip_again_so_soon_after_the_last_time() {
    var gluetun = await WithGluetunAsync();
    Options.RotateMinInterval = TimeSpan.FromMinutes(1);
    Assert.True((await JobAsync("/list", new { url = "https://video.test/rate-limited", limit = 1 })).EndedRateLimited());
    Assert.Equal(1, gluetun.Stops);
  }

  [Fact]
  public async Task Gives_up_if_gluetun_cant_be_told_to_reconnect() {
    var gluetun = await WithGluetunAsync(apiKey: "other-key");
    Assert.True((await JobAsync("/list", new { url = "https://video.test/rate-limited", limit = 1 })).EndedRateLimited());
    Assert.Equal(0, gluetun.Stops);
  }

  [Fact]
  public async Task Sweeps_away_downloads_nobody_collected() {
    var (id, ext) = (await JobAsync("/download", new { url = "https://video.test/watch" })).File();
    var path = Path.Join(Options.WorkDir, $"{id}.{ext}");
    var store = Service<DownloadStore>();
    store.Sweep();
    Assert.True(File.Exists(path));
    File.SetLastWriteTimeUtc(path, DateTime.UtcNow - Options.FileTtl - TimeSpan.FromSeconds(1));
    store.Sweep();
    Assert.False(File.Exists(path));
    Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/files/{id}")).StatusCode);
  }
}
