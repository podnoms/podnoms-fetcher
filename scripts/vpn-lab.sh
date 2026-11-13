#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
Usage: scripts/vpn-lab.sh [--keep]

Checks the fetcher's own VPN (docker/fetcher-vpn) for real, without a VPN
provider: it builds the image, starts two WireGuard servers and the fetcher in
containers, and checks that

  - the fetcher comes up through one of the servers;
  - PodNoms can still call it, from this host and from another network;
  - a refused job moves it to the other server, and so another public IP;
  - nothing leaves the fetcher except through the tunnel (the kill switch),
    with the servers up and with them gone.

Needs Docker on a Linux host whose kernel has WireGuard. Everything it makes
is removed afterwards; --keep leaves it running to look at.

  inside   172.31.250.0/24  the fetcher (.30), the servers (.11, .12), a bystander (.21)
  outside  172.31.251.0/24  the servers (.11, .12) and the "internet" (.20), which
                            answers with the address it was called from
EOF
}

keep=false
for argument in "$@"; do
  case "$argument" in
    --help|-h) usage; exit 0 ;;
    --keep) keep=true ;;
    *) echo "Unknown option: $argument. Use --help for usage." >&2; exit 1 ;;
  esac
done

repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
image=podnoms-fetcher:vpn-lab
prefix=fetcher-lab
secret=vpn-lab-secret-0123456789
port=18097
work=$(mktemp -d)
failures=0

cleanup() {
  if "$keep"; then
    echo "Left running. Remove with: docker rm -f \$(docker ps -aq --filter name=$prefix-); docker network rm $prefix-inside $prefix-outside"
    return
  fi
  docker rm -f $(docker ps -aq --filter "name=$prefix-") >/dev/null 2>&1 || true
  docker network rm "$prefix-inside" "$prefix-outside" >/dev/null 2>&1 || true
  docker rmi "$image" >/dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT

pass() { printf '  ok    %s\n' "$1"; }
fail() { printf '  FAIL  %s\n' "$1"; failures=$((failures + 1)); }
# check DESCRIPTION COMMAND...: passes if the command succeeds.
check() { local what=$1; shift; if "$@" >/dev/null 2>&1; then pass "$what"; else fail "$what"; fi; }
# refuse DESCRIPTION COMMAND...: passes if the command fails.
refuse() { local what=$1; shift; if "$@" >/dev/null 2>&1; then fail "$what"; else pass "$what"; fi; }

call() { curl -fsS -m 10 -H "Authorization: Bearer $secret" "$@"; }
public_ip() { call "http://127.0.0.1:$port/health" | sed -E 's/.*"publicIp":"?([^",}]*)"?.*/\1/'; }
# Fetches a URL from inside the fetcher, as the app's user.
from_fetcher() { docker exec "$prefix-fetcher" wget -q -T 4 -O - "$1"; }

echo "Building $image"
docker build -q -t "$image" "$repo_root" >/dev/null
tool() { docker run --rm -i --entrypoint "$1" "$image" "${@:2}"; }

docker network create --subnet 172.31.250.0/24 "$prefix-inside" >/dev/null
docker network create --subnet 172.31.251.0/24 "$prefix-outside" >/dev/null

client_key=$(tool wg genkey)
client_public=$(echo "$client_key" | tool wg pubkey)

# Answers with the caller's address, as PUBLIC_IP_URL's service does.
echo_server='
import http.server
class Echo(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200); self.end_headers(); self.wfile.write(self.client_address[0].encode())
    def log_message(self, *args): pass
http.server.HTTPServer(("0.0.0.0", 8000), Echo).serve_forever()'
docker run -d --name "$prefix-internet" --network "$prefix-outside" --ip 172.31.251.20 --entrypoint python3 "$image" -c "$echo_server" >/dev/null
docker run -d --name "$prefix-bystander" --network "$prefix-inside" --ip 172.31.250.21 --entrypoint python3 "$image" -c "$echo_server" >/dev/null

mkdir -p "$work/wireguard"
for n in 1 2; do
  server_key=$(tool wg genkey)
  server_public=$(echo "$server_key" | tool wg pubkey)
  docker create --name "$prefix-server-$n" --user root --cap-add NET_ADMIN --sysctl net.ipv4.ip_forward=1 \
    --network "$prefix-inside" --ip "172.31.250.1$n" -e "KEY=$server_key" --entrypoint sh "$image" -c "
      echo \"\$KEY\" > /key
      ip link add wg0 type wireguard
      wg set wg0 listen-port 51820 private-key /key peer '$client_public' allowed-ips 10.2.0.2/32
      ip addr add 10.2.0.1/24 dev wg0
      ip link set wg0 up
      iptables -t nat -A POSTROUTING -s 10.2.0.0/24 -j MASQUERADE
      exec sleep infinity" >/dev/null
  docker network connect --ip "172.31.251.1$n" "$prefix-outside" "$prefix-server-$n"
  docker start "$prefix-server-$n" >/dev/null
  # As a provider's download.
  cat > "$work/wireguard/server-$n.conf" <<EOF
[Interface]
PrivateKey = $client_key
Address = 10.2.0.2/32
DNS = 10.2.0.1

[Peer]
PublicKey = $server_public
AllowedIPs = 0.0.0.0/0
Endpoint = 172.31.250.1$n:51820
EOF
done

# Lists one entry, unless our public IP is the one in /tmp/refused.
cat > "$work/yt-dlp" <<'EOF'
#!/bin/sh
[ "$1" = --version ] && { echo vpn-lab; exit 0; }
ip=$(wget -q -T 4 -O - "$PUBLIC_IP_URL") || { echo "ERROR: no way out" >&2; exit 1; }
if [ "$ip" = "$(cat /tmp/refused 2>/dev/null)" ]; then
  echo "ERROR: [youtube] lab: Sign in to confirm you're not a bot" >&2
  exit 1
fi
echo "ENTRY {\"id\":\"listed-from-$ip\"}"
EOF
chmod -R a+rX "$work"
chmod a+x "$work/yt-dlp"

docker run -d --name "$prefix-fetcher" --cap-add NET_ADMIN --sysctl net.ipv4.conf.all.src_valid_mark=1 \
  --network "$prefix-inside" --ip 172.31.250.30 -p "127.0.0.1:$port:8080" \
  -v "$work/wireguard:/etc/fetcher/wireguard:ro" -v "$work/yt-dlp:/lab/yt-dlp:ro" \
  -e "FETCHER_SECRET=$secret" -e VPN_PROVIDER=files -e PUBLIC_IP_URL=http://172.31.251.20:8000/ \
  -e YTDLP_PATH=/lab/yt-dlp -e YTDLP_UPDATE_ON_START=false -e ROTATE_MIN_INTERVAL_SECONDS=0 \
  "$image" >/dev/null

for _ in $(seq 1 40); do
  call "http://127.0.0.1:$port/health" >/dev/null 2>&1 && break
  sleep 0.5
done

echo 'Coming up'
first=$(public_ip || true)
case "$first" in
  172.31.251.11|172.31.251.12) pass "public IP is a server's: $first" ;;
  *) fail "public IP should be a server's, but is '$first'"; docker logs "$prefix-fetcher" 2>&1 | tail -5 ;;
esac
check 'runs as the app user' test "$(docker exec "$prefix-fetcher" id -un)" = app
check 'names are looked up through the tunnel' docker exec "$prefix-fetcher" grep -qx 'nameserver 10.2.0.1' /etc/resolv.conf

echo 'Being called'
check 'from this host, through the published port' call "http://127.0.0.1:$port/health"
host_ip=$(ip route get 1.1.1.1 2>/dev/null | sed -nE 's/.* src ([0-9.]+).*/\1/p')
if [ -n "$host_ip" ]; then
  check "from another network ($host_ip), answered the way it came" call --interface "$host_ip" http://172.31.250.30:8080/health
else
  echo '  skip  from another network (no address of this host to call from)'
fi
refuse 'not on any other port' curl -fsS -m 3 http://172.31.250.30:8000/

echo 'Kill switch, with the tunnel up'
check 'reaches the internet through the tunnel' from_fetcher http://172.31.251.20:8000/
if docker run --rm --network "$prefix-inside" --entrypoint wget "$image" -q -T 4 -O - http://172.31.250.21:8000/ >/dev/null 2>&1; then
  refuse 'cannot reach its own network outside the tunnel' from_fetcher http://172.31.250.21:8000/
else
  fail 'the bystander is not answering, so the leak check means nothing'
fi

echo 'Being refused'
docker exec "$prefix-fetcher" sh -c "echo $first > /tmp/refused"
listing=$(call -H 'Content-Type: application/json' -d '{"url":"https://video.test/channel","limit":1}' "http://127.0.0.1:$port/list" || true)
second=$(public_ip || true)
check 'the job succeeds after moving' test "$(echo "$listing" | tail -1)" = '{"end":true}'
check "moved to the other server: $first -> $second" test "$second" != "$first" -a "${second#172.31.251.1}" != "$second"
check 'the job ran from the new IP' grep -q "listed-from-$second" <<<"$listing"
check 'only one server may be sent to' test "$(docker exec -u root "$prefix-fetcher" iptables -S FETCHER_SERVER | grep -c -- '-j ACCEPT')" = 1

echo 'Kill switch, with the servers gone'
docker stop -t 1 "$prefix-server-1" "$prefix-server-2" >/dev/null
refuse 'cannot reach the internet' from_fetcher http://172.31.251.20:8000/
refuse 'cannot reach its own network' from_fetcher http://172.31.250.21:8000/
check 'can still be called' call "http://127.0.0.1:$port/health"

echo
if [ "$failures" -eq 0 ]; then
  echo 'All checks passed.'
else
  echo "$failures check(s) failed. The fetcher's log:"
  docker logs "$prefix-fetcher" 2>&1 | tail -20
  exit 1
fi
