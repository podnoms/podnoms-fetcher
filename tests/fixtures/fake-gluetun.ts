// Stands in for gluetun's control server. Each reconnect (stopped, then
// running) moves to the next IP, which it writes to FAKE_IP_FILE for the fake
// yt-dlp to read.
import { writeFileSync } from 'node:fs'
import { createServer } from 'node:http'
import type { AddressInfo } from 'node:net'

export async function startFakeGluetun({ apiKey = 'gluetun-key', ips = ['10.0.0.1', '10.0.0.2', '10.0.0.3', '10.0.0.4'] } = {}) {
  const state = { index: 0, stops: 0, running: true }
  const ip = () => (state.running ? ips[Math.min(state.index, ips.length - 1)]! : '')
  writeFileSync(process.env.FAKE_IP_FILE!, ip())
  const server = createServer((req, res) => {
    if (req.headers['x-api-key'] !== apiKey) {
      res.writeHead(401).end()
      return
    }
    let body = ''
    req.on('data', (chunk) => (body += chunk))
    req.on('end', () => {
      if (req.method === 'GET' && req.url === '/v1/publicip/ip') {
        res.writeHead(200, { 'Content-Type': 'application/json' }).end(JSON.stringify({ public_ip: ip() }))
        return
      }
      if (req.method === 'PUT' && req.url === '/v1/vpn/status') {
        const { status } = JSON.parse(body) as { status: string }
        if (status === 'stopped') {
          state.running = false
          state.stops++
        } else {
          state.running = true
          state.index++
        }
        writeFileSync(process.env.FAKE_IP_FILE!, ip())
        res.writeHead(200, { 'Content-Type': 'application/json' }).end(JSON.stringify({ outcome: status }))
        return
      }
      res.writeHead(404).end()
    })
  })
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
  const { port } = server.address() as AddressInfo
  return {
    url: `http://127.0.0.1:${port}`,
    apiKey,
    state,
    close: () => new Promise<void>((resolve) => server.close(() => resolve())),
  }
}
