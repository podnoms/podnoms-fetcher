# podnoms-fetcher

Runs [PodNoms](https://github.com/podnoms/podnoms-platform)' yt-dlp jobs on another host, behind a [gluetun](https://github.com/qdm12/gluetun) VPN. When a platform refuses it (YouTube's "Sign in to confirm you're not a bot", HTTP 429), it has gluetun reconnect to a new server, so it gets a new IP, and tries again.

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

Requests never carry yt-dlp arguments. The fetcher builds the same arguments PodNoms uses for local runs (`src/ytdlp.ts`), so they print the same lines.

## Configuration

| Variable | Default | |
| --- | --- | --- |
| `FETCHER_SECRET` | required | At least 16 characters; the same value as PodNoms' `FETCHER_SECRET` |
| `PORT` | `8080` | |
| `YTDLP_PATH` | `yt-dlp` | |
| `WORK_DIR` | `/tmp/podnoms-fetcher` | Where downloads wait to be collected |
| `GLUETUN_CONTROL_URL` | none | e.g. `http://localhost:8000`. Without it, refusals go straight back to PodNoms |
| `GLUETUN_API_KEY` | none | The key in gluetun's `HTTP_CONTROL_SERVER_AUTH_DEFAULT_ROLE` |
| `ROTATE_RETRIES` | `2` | New IPs one job may try |
| `ROTATE_MIN_INTERVAL_SECONDS` | `120` | No new rotation sooner than this after the last one |
| `ROTATE_TIMEOUT_SECONDS` | `90` | How long to wait for gluetun's new IP |
| `YTDLP_UPDATE_ON_START` | `true` in Docker | Updates yt-dlp each time the container starts |
| `YTDLP_PIP` | `/opt/yt-dlp/bin/pip` in Docker | pip in yt-dlp's virtualenv; with it, yt-dlp is also updated while running |
| `YTDLP_UPDATE_INTERVAL_HOURS` | `4` | How often that is; it waits for a moment with no jobs running. `0` turns it off |

## Running it

```sh
bun install
FETCHER_SECRET=… bun run dev      # http://localhost:8080
bun run test
bun run typecheck
```

The image is published to `ghcr.io/podnoms/podnoms-fetcher` (`latest`, and the version) by the GitHub Actions workflow when a `v*` tag is pushed, after the tests pass.

To deploy, use [compose.example.yml](compose.example.yml). It runs gluetun with the fetcher sharing its network, so all the fetcher's traffic goes through the VPN, and publishes the fetcher's port on gluetun. Choose a wide `SERVER_COUNTRIES` list so each reconnect is likely to land on a new IP. PodNoms must be able to reach the port, for example over Tailscale. Then set this in PodNoms:

```
FETCHER_URL=http://<this host>:8080
FETCHER_SECRET=<the same secret>
```

PodNoms' admin page shows the fetcher's yt-dlp version and current public IP.

## License

[MIT](LICENSE) © 2026 Fergal Moran
