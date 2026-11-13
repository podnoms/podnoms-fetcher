using Microsoft.Extensions.Configuration;

namespace PodNoms.Fetcher.Tests;

public sealed class VpnTests : FetcherTest {
  [Fact]
  public void Reads_a_providers_wireguard_config() {
    var server = VpnServers.ParseConfig("nl-1", FakeVpn.Config("server-key", "203.0.113.7"));
    // The IPv4 address of the two; the tunnel is IPv4.
    Assert.Equal(new VpnServer("nl-1", "server-key", "203.0.113.7:51820", "private-server-key", "10.2.0.2/32", "10.2.0.1"), server);
  }

  [Theory]
  [InlineData("")]
  [InlineData("[Interface]\nPrivateKey = a\nAddress = 10.2.0.2/32\nDNS = 10.2.0.1\n")]
  [InlineData("[Interface]\nAddress = 10.2.0.2/32\nDNS = 10.2.0.1\n[Peer]\nPublicKey = b\nEndpoint = 203.0.113.7:51820\n")]
  [InlineData("[Interface]\nPrivateKey = a\nAddress = fd00::2/128\nDNS = 10.2.0.1\n[Peer]\nPublicKey = b\nEndpoint = 203.0.113.7:51820\n")]
  [InlineData("[Interface]\nPrivateKey = a\nAddress = 10.2.0.2/32\nDNS = 10.2.0.1\n[Peer]\nPublicKey = b\nEndpoint = 203.0.113.7\n")]
  public void Passes_over_configs_it_cant_use(string config) {
    Assert.Null(VpnServers.ParseConfig("broken", config));
  }

  [Fact]
  public void Takes_mullvads_active_servers_in_the_countries_asked_for() {
    // As https://api.mullvad.net/www/relays/wireguard/ answers.
    const string relays = """
        [
          {"hostname":"nl-ams-wg-001","country_code":"nl","country_name":"Netherlands","active":true,"ipv4_addr_in":"203.0.113.1","pubkey":"nl-key"},
          {"hostname":"nl-ams-wg-002","country_code":"nl","country_name":"Netherlands","active":false,"ipv4_addr_in":"203.0.113.2","pubkey":"off-key"},
          {"hostname":"ie-dub-wg-101","country_code":"ie","country_name":"Ireland","active":true,"ipv4_addr_in":"203.0.113.3","pubkey":"ie-key"},
          {"hostname":"us-nyc-wg-301","country_code":"us","country_name":"USA","active":true,"ipv4_addr_in":"203.0.113.4","pubkey":"us-key"}
        ]
        """;
    var mullvad = new VpnOptions { Provider = "mullvad", PrivateKey = "ours", Address = "10.66.1.2/32", Countries = ["Netherlands", "IE"] };
    Assert.Equal(
        [new VpnServer("nl-ams-wg-001", "nl-key", "203.0.113.1:51820", "ours", "10.66.1.2/32", "10.64.0.1"), new VpnServer("ie-dub-wg-101", "ie-key", "203.0.113.3:51820", "ours", "10.66.1.2/32", "10.64.0.1")],
        VpnServers.FromMullvad(relays, mullvad));
    Assert.Equal(3, VpnServers.FromMullvad(relays, mullvad with { Countries = [] }).Count);
  }

  [Fact]
  public void Takes_protons_servers_in_the_countries_asked_for() {
    const string list = """
        {"servers": [
          {"name": "node-de-01", "country": "Germany", "publicKey": "de-key", "ip": "203.0.113.1"},
          {"name": "node-cz-01", "country": "Czech Republic", "publicKey": "cz-key", "ip": "203.0.113.2"},
          {"name": "node-us-01", "country": "United States", "publicKey": "us-key", "ip": "203.0.113.3"}
        ]}
        """;
    var proton = new VpnOptions { Provider = "protonvpn", PrivateKey = "ours", Countries = ["germany", "Czech Republic"] };
    Assert.Equal(
        [new VpnServer("node-de-01", "de-key", "203.0.113.1:51820", "ours", "10.2.0.2/32", "10.2.0.1"), new VpnServer("node-cz-01", "cz-key", "203.0.113.2:51820", "ours", "10.2.0.2/32", "10.2.0.1")],
        VpnServers.FromProton(list, proton));
  }

  [Fact]
  public void Carries_a_list_of_protons_servers_the_helper_will_take() {
    var servers = VpnServers.FromProton(VpnServers.ProtonServers(), new VpnOptions { Provider = "protonvpn", PrivateKey = "ours" });
    Assert.True(servers.Count > 300, $"Only {servers.Count} servers");
    Assert.Contains(VpnServers.FromProton(VpnServers.ProtonServers(), new VpnOptions { Provider = "protonvpn", PrivateKey = "ours", Countries = ["Germany"] }), _ => true);
    // As docker/fetcher-vpn checks them.
    Assert.All(servers, server => Assert.Matches(@"^[A-Za-z0-9+/]{43}=$", server.PublicKey));
    Assert.All(servers, server => Assert.Matches(@"^(\d{1,3}\.){3}\d{1,3}:51820$", server.Endpoint));
    Assert.Equal(servers.Count, servers.Select(server => server.Endpoint).Distinct().Count());
  }

  [Fact]
  public async Task Starts_on_a_server_that_comes_up() {
    await WithVpnAsync("192.0.2.1", "10.0.0.5", "192.0.2.2");
    Assert.Equal("10.0.0.5", Vpn.PublicIp);
    Assert.Equal("server-2", Vpn.Server?.Name);
  }

  [Fact]
  public async Task Wont_start_if_no_server_comes_up() {
    var error = await Assert.ThrowsAsync<InvalidOperationException>(() => WithVpnAsync("192.0.2.1", "192.0.2.2"));
    Assert.Contains("None of the 2 VPN servers tried", error.Message);
    Assert.Null(Vpn.PublicIp);
  }

  [Fact]
  public async Task Wont_start_if_the_tunnel_cant_be_made() {
    var vpn = FakeVpn.WithServers("10.0.0.1", "10.0.0.2");
    await using (vpn) {
      Options.Vpn = vpn.Options with { Helper = "/bin/false" };
      var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Vpn.StartAsync());
      Assert.Contains("Could not change the VPN tunnel", error.Message);
    }
  }

  [Fact]
  public async Task Wont_start_without_servers() {
    var error = await Assert.ThrowsAsync<InvalidOperationException>(() => WithVpnAsync());
    Assert.Contains("No usable WireGuard configs", error.Message);
  }

  [Fact]
  public void Reads_its_settings() {
    var settings = new Dictionary<string, string?> {
      ["FETCHER_SECRET"] = TestEnvironment.Secret,
      ["VPN_PROVIDER"] = "Mullvad",
      ["WIREGUARD_PRIVATE_KEY"] = "ours",
      ["WIREGUARD_ADDRESSES"] = "fc00:bbbb::1/128, 10.66.1.2/32",
      ["SERVER_COUNTRIES"] = "Netherlands, Ireland",
    };
    FetcherOptions Load() => FetcherOptions.Load(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

    var vpn = Load().Vpn!;
    Assert.Equal(("mullvad", "ours", "10.66.1.2/32"), (vpn.Provider, vpn.PrivateKey, vpn.Address));
    Assert.Equal(["Netherlands", "Ireland"], vpn.Countries);

    settings.Remove("WIREGUARD_PRIVATE_KEY");
    Assert.Contains("needs WIREGUARD_PRIVATE_KEY", Assert.Throws<InvalidOperationException>(Load).Message);
    settings["VPN_PROVIDER"] = "gluetun";
    Assert.Contains("must be files, protonvpn or mullvad", Assert.Throws<InvalidOperationException>(Load).Message);
    settings.Remove("VPN_PROVIDER");
    Assert.Null(Load().Vpn);
  }
}
