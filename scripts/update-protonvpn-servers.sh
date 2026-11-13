#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF2'
Usage: scripts/update-protonvpn-servers.sh [GLUETUN_TAG]

Rewrites src/ProtonVpnServers.json, the Proton VPN servers the fetcher can use
with VPN_PROVIDER=protonvpn, from the server list in a gluetun release
(default v3.41.3). Proton's own list needs an account login to read, and
gluetun's is the one this deployment used before the fetcher ran its own VPN.

Kept: paid WireGuard servers that are neither Secure Core nor Tor.

Gluetun stopped shipping the list after the v3.41 releases, so newer tags have
none. To refresh it later, make one with gluetun's updater and a Proton login
(see the gluetun wiki, "Updating servers") and pass the file instead:

  scripts/update-protonvpn-servers.sh path/to/servers.json
EOF2
}

source=${1:-v3.41.3}
case "$source" in --help|-h) usage; exit 0 ;; esac

repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
list=$(mktemp)
trap 'rm -f "$list"' EXIT
if [ -f "$source" ]; then
  cp "$source" "$list"
else
  curl -fsSL -o "$list" "https://raw.githubusercontent.com/qdm12/gluetun/$source/internal/storage/servers.json"
fi

python3 - "$list" "$repo_root/src/ProtonVpnServers.json" "$source" <<'EOF2'
import datetime, json, sys

source, target, origin = sys.argv[1:]
proton = json.load(open(source))["protonvpn"]
servers = [
    {"name": server["hostname"].removesuffix(".protonvpn.net"), "country": server["country"], "publicKey": server["wgpubkey"], "ip": ip}
    for server in proton["servers"]
    if server.get("vpn") == "wireguard" and not (server.get("free") or server.get("secure_core") or server.get("tor"))
    for ip in server["ips"]
    if ":" not in ip
]
servers.sort(key=lambda server: (server["country"], server["name"], server["ip"]))
listed = datetime.datetime.fromtimestamp(proton["timestamp"], datetime.UTC).date().isoformat()
with open(target, "w") as out:
    out.write('{\n  "source": %s,\n  "listed": %s,\n  "servers": [\n' % (json.dumps("gluetun " + origin), json.dumps(listed)))
    out.write(",\n".join("    " + json.dumps(server) for server in servers))
    out.write("\n  ]\n}\n")
print(f"{len(servers)} servers in {len({server['country'] for server in servers})} countries, listed {listed}")
EOF2
