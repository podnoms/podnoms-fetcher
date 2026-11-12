// Stands in for yt-dlp in tests (YTDLP_PATH, see TestEnvironment.cs). It
// answers like the real one does to the arguments src/YtDlp.cs passes, and
// behaves according to the link:
//   /fail                  fails as an unavailable video
//   /rate-limited          fails as the platform refusing us, always
//   ?refuse=ip,ip          refuses us while our public IP (FAKE_IP_FILE) is one of these
//   ?hold=ms               pauses after reporting download progress
//   /no-info               downloads without saying where it saved the file
// Given --flat-playlist, it lists a channel: ?entries=c,b,a
using System.Text.Json;
using System.Web;

static string? ReadIfThere(string variable) =>
    Environment.GetEnvironmentVariable(variable) is { Length: > 0 } path && File.Exists(path) ? File.ReadAllText(path) : null;

static void Print(string prefix, object value) => Console.WriteLine($"{prefix} {JsonSerializer.Serialize(value)}");

if (args[0] == "--version") {
  // What fake-pip last "installed", if it has.
  Console.WriteLine(ReadIfThere("FAKE_PIP_VERSION_FILE") ?? "2026.08.19");
  return 0;
}

var url = new Uri(args[^1]);
var query = HttpUtility.ParseQueryString(url.Query);
var ip = ReadIfThere("FAKE_IP_FILE") ?? "";
var refuse = query["refuse"]?.Split(',') ?? [];

if (url.AbsolutePath == "/fail") {
  Console.Error.WriteLine("ERROR: [youtube] abc123: This video is unavailable");
  return 1;
}
if (url.AbsolutePath == "/rate-limited" || refuse.Contains(ip)) {
  Console.Error.WriteLine("ERROR: [youtube] abc123: Sign in to confirm you're not a bot. Use --cookies-from-browser or --cookies");
  return 1;
}

if (args.Contains("--flat-playlist")) {
  var end = Array.IndexOf(args, "--playlist-end") is var at and >= 0 ? int.Parse(args[at + 1]) : int.MaxValue;
  var ids = query["entries"]?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [];
  foreach (var id in ids.Take(end))
    Print("ENTRY", new { id, url = $"https://video.test/watch?v={id}", title = $"Video {id}", duration = 600, live_status = "not_live" });
  Print("PLAYLIST", new { title = "Test Channel - Videos", thumbnails = Array.Empty<object>() });
  return 0;
}

var output = args[Array.IndexOf(args, "--output") + 1];
Print("DETAILS", new { title = "Test video", duration = 3 });
foreach (var downloaded in new[] { 1000, 2000, 3000 })
  Print("PROGRESS", new { status = "downloading", downloaded_bytes = downloaded, total_bytes = 3000, speed = 1000, eta = 0 });
await Task.Delay(int.Parse(query["hold"] ?? "0"));
var filepath = output.Replace("%(ext)s", "m4a");
File.WriteAllText(filepath, "fake audio");
if (url.AbsolutePath == "/no-info") Print("INFO", new { title = "Test video", duration = 3 });
else Print("INFO", new { title = "Test video", duration = 3, filepath });
return 0;
