# podnoms-fetcher

Runs [PodNoms](https://github.com/podnoms/podnoms-platform)' yt-dlp jobs on another host, through a WireGuard VPN it runs itself. When a platform refuses it (YouTube's "Sign in to confirm you're not a bot", HTTP 429), it moves the VPN to another server, so it gets a new IP, and tries again.

PodNoms keeps everything else: the download queue and throttle, conversion to MP3, and the database. It calls the fetcher when `FETCHER_URL` and `FETCHER_SECRET` are set, and otherwise runs yt-dlp itself.

## API

Every request needs `Authorization: Bearer $FETCHER_SECRET`.

| Route | Does |
| --- | --- |
| `POST /list` `{ url, limit }` | Lists a channel or playlist (`limit: null` for all of it) |
| `POST /download` `{ url, limitRate? }` | Downloads the source's audio (`limitRate` as yt-dlp's `--limit-rate`, e.g. `2M`) |
| `GET /files/:id` | The finished download |
| `DELETE /files/:id` | Removes it. Uncollected downloads are removed after an hour anyway |
| `GET /health` | `{ ytDlpVersion, publicIp }` |

Jobs answer with newline-delimited JSON. First comes a `{ "line": "…" }` for each line yt-dlp prints. Then exactly one of these:
- `{ "end": true }`: the listing finished.
- `{ "file": { "id", "ext" } }`: the download finished; collect it from `/files/:id`.
- `{ "error", "rateLimited" }`: the job failed. `rateLimited` is true when the platform still refused after the IP changes.

Requests never carry yt-dlp arguments. The fetcher builds the same arguments PodNoms uses for local runs (`src/YtDlp.cs`), so they print the same lines.

## Configuration

| Variable | Default | |
| --- | --- | --- |
| `FETCHER_SECRET` | required | At least 16 characters; the same value as PodNoms' `FETCHER_SECRET` |
| `PORT` | `8080` | |
| `YTDLP_PATH` | `yt-dlp` | |
| `WORK_DIR` | `/tmp/podnoms-fetcher` | Where downloads wait to be collected |
| `VPN_PROVIDER` | none | `protonvpn`, `mullvad` or `files`: see [The VPN](#the-vpn). Without it there is no VPN, and refusals go straight back to PodNoms |
| `WIREGUARD_CONFIG_DIR` | `/etc/fetcher/wireguard` | For `files`: the folder of WireGuard configs, one per server |
| `WIREGUARD_PRIVATE_KEY` | none | For `protonvpn` and `mullvad`: the key from a WireGuard config made in your account |
| `WIREGUARD_ADDRESSES` | none | For `mullvad`: the address from the same config |
| `SERVER_COUNTRIES` | all | For `protonvpn` and `mullvad`: the countries to use, by name, e.g. `Netherlands,Germany,Czech Republic` (Mullvad's two-letter codes work too) |
| `PUBLIC_IP_URL` | `https://api.ipify.org` | Answers with the address it's called from, as plain text. It's how the fetcher knows a server is up and what its IP is |
| `ROTATE_RETRIES` | `2` | New IPs one job may try |
| `ROTATE_MIN_INTERVAL_SECONDS` | `120` | No new rotation sooner than this after the last one |
| `ROTATE_TIMEOUT_SECONDS` | `90` | How long to go on trying servers for a new IP |
| `YTDLP_UPDATE_ON_START` | `true` in Docker | Updates yt-dlp before taking jobs (needs `YTDLP_PIP`) |
| `YTDLP_PIP` | `/opt/yt-dlp/bin/pip` in Docker | pip in yt-dlp's virtualenv; with it, yt-dlp is also updated while running |
| `YTDLP_UPDATE_INTERVAL_HOURS` | `4` | How often that is; it waits for a moment with no jobs running. `0` turns it off |

## Running it

It's an ASP.NET Core app, and needs the .NET 11 SDK.

```sh
FETCHER_SECRET=… dotnet watch --project src      # http://localhost:8080
dotnet test
```

The image is published to `ghcr.io/podnoms/podnoms-fetcher` (`latest`, and the version) by the GitHub Actions workflow when a `v*` tag is pushed, after the tests pass.

To publish from your own machine instead, `scripts/build-docker.sh` builds the current checkout and pushes it as `latest` (or the tag given), without the tests; `--dry-run` shows what it would do. It needs `docker login ghcr.io` with a token that has `write:packages`.

## The VPN

The fetcher makes the WireGuard tunnel itself, in its own container, and chooses which of your provider's servers to be on. To change IP it moves to the server it has been off longest, and tries the next if that one doesn't come up or has the IP it was just refused on.

It's also the kill switch: everything the container sends goes through the tunnel, except the WireGuard packets themselves and answers to PodNoms' calls. If the tunnel is down nothing gets out, and it won't start until a server is up. `docker/fetcher-vpn` does this, and is the only thing in the image that runs as root.

The container needs the `NET_ADMIN` capability and the `net.ipv4.conf.all.src_valid_mark=1` sysctl, and the host's kernel needs WireGuard (Linux 5.6 or later). Only WireGuard is supported, and only IPv4.

Where the servers come from is `VPN_PROVIDER`:

- `protonvpn`: Proton VPN's paid servers, from a list built into the fetcher. Set `WIREGUARD_PRIVATE_KEY` to the `PrivateKey` of any WireGuard config made at account.protonvpn.com (one key works on every server), and `SERVER_COUNTRIES` to narrow the choice. Proton doesn't publish its servers without an account login, so the list (`src/ProtonVpnServers.json`) is the one gluetun shipped, dated in the file; servers that have gone since are passed over as they fail to come up. `scripts/update-protonvpn-servers.sh` rewrites it.
- `files`: a folder of WireGuard configs as your provider gives them out, one `.conf` per server, mounted at `/etc/fetcher/wireguard`. This works with any provider (Proton VPN, for example: download a config for each server you want to use). The more configs, the more IPs to move between; new ones are picked up within a day.
- `mullvad`: Mullvad's own list of servers, fetched as the fetcher starts, so there is no folder to keep. Set `WIREGUARD_PRIVATE_KEY` and `WIREGUARD_ADDRESSES` from a WireGuard config Mullvad makes for your account, and `SERVER_COUNTRIES` to narrow the choice.

`scripts/vpn-lab.sh` checks all of this for real, with WireGuard servers of its own in containers: that the fetcher comes up through one, moves to another when refused, can still be called, and can't send anything outside the tunnel.

## Deploying

Use [compose.example.yml](compose.example.yml), which is set up for Proton VPN. PodNoms must be able to reach the port, for example over Tailscale. Then set this in PodNoms:

```
FETCHER_URL=http://<this host>:8080
FETCHER_SECRET=<the same secret>
```

PodNoms' admin page shows the fetcher's yt-dlp version and current public IP.

## License

[MIT](LICENSE) © 2026 Fergal Moran
