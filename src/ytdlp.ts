// The yt-dlp runs PodNoms asks for, with the same arguments and the same
// printed lines (DETAILS, PROGRESS, INFO, ENTRY, PLAYLIST) PodNoms runs locally
// without a fetcher, so its parsing is shared. Requests never carry arguments
// of their own.
import { spawn } from 'node:child_process'
import { config } from './config'

function politeArgs(limitRate?: string) {
  return [
    // One extraction makes several requests; pause between them.
    '--sleep-requests',
    '1',
    // Fewer retries than the default 10, each waiting longer than the last.
    '--retries',
    '3',
    '--retry-sleep',
    'exp=1:60',
    ...(limitRate ? ['--limit-rate', limitRate] : []),
  ]
}

// As episode-processor.server.ts in PodNoms.
export function downloadArgs(url: string, outputTemplate: string, limitRate?: string) {
  return [
    ...politeArgs(limitRate),
    '--no-playlist',
    '--progress',
    '--newline',
    '--progress-template',
    'download:PROGRESS %(progress.{status,downloaded_bytes,total_bytes,total_bytes_estimate,speed,eta})j',
    '--no-simulate',
    '--format',
    'bestaudio/best',
    '--output',
    outputTemplate,
    '--print',
    'video:DETAILS %(.{title,description,duration,thumbnail,timestamp,upload_date})j',
    '--print',
    'after_move:INFO %(.{title,description,duration,thumbnail,filepath,timestamp,upload_date})j',
    url,
  ]
}

// As channel-listing.server.ts in PodNoms.
export function listArgs(url: string, limit: number | null) {
  return [
    ...politeArgs(),
    '--flat-playlist',
    '--lazy-playlist',
    ...(limit === null ? [] : ['--playlist-end', String(limit)]),
    '--print',
    'ENTRY %(.{id,url,webpage_url,title,duration,live_status})j',
    '--print',
    'playlist:PLAYLIST %(.{title,description,thumbnails})j',
    url,
  ]
}

// The platform refusing us, rather than something wrong with the one video.
const rateLimited = [/HTTP Error 429/i, /Too Many Requests/i, /confirm you.?re not a bot/i, /rate[- ]?limit/i]

export function isRateLimited(message: string) {
  return rateLimited.some((pattern) => pattern.test(message))
}

// "ERROR: [youtube] abc123: This video is unavailable" → "This video is unavailable"
export function errorMessage(line: string) {
  return line.replace(/^ERROR:\s*(\[[^\]]+\]\s*[^:\s]+:\s*)?/, '')
}

export type RunResult = { ok: true } | { ok: false; error: string; rateLimited: boolean }

// Runs yt-dlp, handing each line of output (on either stream, errors aside)
// to onLine. Stopped if the signal aborts, e.g. when PodNoms hangs up.
export function runYtDlp(args: string[], onLine: (line: string) => void, signal?: AbortSignal) {
  return new Promise<RunResult>((resolve) => {
    const child = spawn(config.ytDlpPath, args, { stdio: ['ignore', 'pipe', 'pipe'], signal })
    const errors: string[] = []
    const readLines = (stream: NodeJS.ReadableStream) => {
      let buffered = ''
      stream.on('data', (chunk) => {
        buffered += chunk
        const lines = buffered.split('\n')
        buffered = lines.pop() ?? ''
        for (const line of lines) {
          if (line.startsWith('ERROR')) errors.push(errorMessage(line))
          else onLine(line)
        }
      })
      stream.on('end', () => {
        if (buffered) onLine(buffered)
      })
    }
    readLines(child.stdout)
    readLines(child.stderr)
    const fail = (error: string) => resolve({ ok: false, error, rateLimited: isRateLimited(error) })
    child.on('error', (error) => {
      if (error.name === 'AbortError') fail('Cancelled')
      else fail(`Could not run yt-dlp: ${error.message}`)
    })
    child.on('close', (code) => {
      if (code === 0) resolve({ ok: true })
      else fail(errors.at(-1) || `yt-dlp exited with code ${code}`)
    })
  })
}

let version: Promise<string | null> | undefined

// After an update, so the next ytDlpVersion() asks yt-dlp again.
export function forgetYtDlpVersion() {
  version = undefined
}

export function ytDlpVersion() {
  version ??= new Promise((resolve) => {
    let output = ''
    const child = spawn(config.ytDlpPath, ['--version'], { stdio: ['ignore', 'pipe', 'ignore'] })
    child.stdout.on('data', (chunk) => (output += chunk))
    child.on('error', () => resolve(null))
    child.on('close', (code) => resolve(code === 0 ? output.trim() : null))
  })
  return version
}
