// Stands in for docker/fetcher-vpn in tests (VpnOptions.Helper). Connecting to
// a server makes its endpoint's address our "public IP", written to
// FAKE_IP_FILE for the fake yt-dlp and the tests' IP echo to read. Servers at
// 192.0.2.x never come up. Each connection is noted in FAKE_VPN_LOG.
if (args is not ["connect", var keyFile, _, _, var publicKey, var endpoint, _]) {
  Console.Error.WriteLine("usage: fake-vpn connect KEYFILE ADDRESS DNS PUBLICKEY ENDPOINT PORT");
  return 1;
}
if (!File.Exists(keyFile) || File.ReadAllText(keyFile).Trim().Length == 0) {
  Console.Error.WriteLine("fake-vpn: can't read the key file");
  return 1;
}

var host = endpoint.Split(':')[0];
File.WriteAllText(Environment.GetEnvironmentVariable("FAKE_IP_FILE")!, host.StartsWith("192.0.2.") ? "" : host);
File.AppendAllText(Environment.GetEnvironmentVariable("FAKE_VPN_LOG")!, $"{publicKey}\n");
return 0;
