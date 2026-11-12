// Stands in for yt-dlp's pip in tests: "upgrades" yt-dlp by writing
// FAKE_PIP_NEW_VERSION to FAKE_PIP_VERSION_FILE, which fake-yt-dlp's --version
// reports, after waiting FAKE_PIP_HOLD_MS. Fails when FAKE_PIP_FAIL is set.
static string? Setting(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

if (Setting("FAKE_PIP_HOLD_MS") is { } hold) await Task.Delay(int.Parse(hold));
if (Setting("FAKE_PIP_FAIL") is not null) {
  Console.Error.WriteLine("ERROR: Could not install packages");
  return 1;
}
if (Setting("FAKE_PIP_VERSION_FILE") is { } file && Setting("FAKE_PIP_NEW_VERSION") is { } version) File.WriteAllText(file, version);
return 0;
