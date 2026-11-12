#!/usr/bin/env node
// Stands in for yt-dlp in tests (YTDLP_PATH, see vitest.config.ts). It
// answers like the real one does to the arguments src/ytdlp.ts passes, and
// behaves according to the link:
//   /fail                  fails as an unavailable video
//   /rate-limited          fails as the platform refusing us, always
//   ?refuse=ip,ip          refuses us while our public IP (FAKE_IP_FILE) is one of these
//   ?hold=ms               pauses after reporting download progress
//   /no-info               downloads without saying where it saved the file
// Given --flat-playlist, it lists a channel: ?entries=c,b,a
import { existsSync, readFileSync, writeFileSync } from 'node:fs'

const args = process.argv.slice(2)
if (args[0] === '--version') {
  // What fake-pip.mjs last "installed", if it has.
  console.log(process.env.FAKE_PIP_VERSION_FILE && existsSync(process.env.FAKE_PIP_VERSION_FILE) ? readFileSync(process.env.FAKE_PIP_VERSION_FILE, 'utf8') : '2026.08.19')
  process.exit(0)
}
const url = new URL(args.at(-1))
const ip = existsSync(process.env.FAKE_IP_FILE) ? readFileSync(process.env.FAKE_IP_FILE, 'utf8') : ''
const refuse = url.searchParams.get('refuse')?.split(',') ?? []

if (url.pathname === '/fail') {
  console.error('ERROR: [youtube] abc123: This video is unavailable')
  process.exit(1)
}
if (url.pathname === '/rate-limited' || refuse.includes(ip)) {
  console.error("ERROR: [youtube] abc123: Sign in to confirm you're not a bot. Use --cookies-from-browser or --cookies")
  process.exit(1)
}

if (args.includes('--flat-playlist')) {
  const end = args.includes('--playlist-end') ? Number(args[args.indexOf('--playlist-end') + 1]) : Infinity
  const ids = url.searchParams.get('entries')?.split(',').filter(Boolean) ?? []
  for (const id of ids.slice(0, end)) {
    console.log(`ENTRY ${JSON.stringify({ id, url: `https://video.test/watch?v=${id}`, title: `Video ${id}`, duration: 600, live_status: 'not_live' })}`)
  }
  console.log(`PLAYLIST ${JSON.stringify({ title: 'Test Channel - Videos', thumbnails: [] })}`)
  process.exit(0)
}

const output = args[args.indexOf('--output') + 1]
const details = { title: 'Test video', duration: 3 }
console.log(`DETAILS ${JSON.stringify(details)}`)
for (const downloaded of [1000, 2000, 3000]) {
  console.log(`PROGRESS ${JSON.stringify({ status: 'downloading', downloaded_bytes: downloaded, total_bytes: 3000, speed: 1000, eta: 0 })}`)
}
await new Promise((resolve) => setTimeout(resolve, Number(url.searchParams.get('hold') ?? 0)))
const filepath = output.replace('%(ext)s', 'm4a')
writeFileSync(filepath, 'fake audio')
console.log(`INFO ${JSON.stringify(url.pathname === '/no-info' ? details : { ...details, filepath })}`)
