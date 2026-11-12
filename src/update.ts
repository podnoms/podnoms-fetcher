// Keeps yt-dlp up to date while the fetcher runs (the entrypoint updates it on
// start too), as YouTube changes often and an old yt-dlp is refused sooner.
// The same as PodNoms' ytdlp-update job: an update only starts while no job
// is running, and no job starts until it's done, so yt-dlp's files are never
// replaced under a running yt-dlp.
import { execFile } from 'node:child_process'
import { promisify } from 'node:util'
import { config } from './config'
import { log } from './log'
import { forgetYtDlpVersion, ytDlpVersion } from './ytdlp'

const run = promisify(execFile)

let active = 0
let updating: Promise<unknown> | null = null

// Wraps a job: waits out an update in progress, and counts it as running.
export async function asJob<T>(work: () => Promise<T>): Promise<T> {
  while (updating) await updating
  active++
  try {
    return await work()
  } finally {
    active--
  }
}

export type UpdateResult = { skipped: 'not configured' | 'jobs running' } | { from: string | null; to: string | null; updated: boolean }

export async function updateYtDlp(): Promise<UpdateResult> {
  const { pip } = config.ytDlpUpdate
  if (!pip) return { skipped: 'not configured' }
  if (active > 0 || updating) return { skipped: 'jobs running' }
  const update = (async () => {
    const from = await ytDlpVersion()
    await run(pip, ['install', '--no-cache-dir', '--quiet', '--disable-pip-version-check', '--upgrade', 'yt-dlp[default]'], {
      timeout: 5 * 60_000,
    })
    forgetYtDlpVersion()
    const to = await ytDlpVersion()
    return { from, to, updated: from !== to }
  })()
  updating = update.catch(() => {})
  try {
    const result = await update
    if (result.updated) log.info(result, 'Updated yt-dlp')
    return result
  } finally {
    updating = null
  }
}

// Every YTDLP_UPDATE_INTERVAL_HOURS, retrying every 10 minutes while jobs
// keep it from running.
export function scheduleUpdates() {
  const { pip, intervalMs } = config.ytDlpUpdate
  if (!pip || !intervalMs) return
  const retryMs = 10 * 60_000
  const tick = async () => {
    let next = intervalMs
    try {
      if ('skipped' in (await updateYtDlp())) next = retryMs
    } catch (error) {
      log.warn({ err: error }, 'Could not update yt-dlp')
    }
    setTimeout(() => void tick(), next).unref()
  }
  setTimeout(() => void tick(), intervalMs).unref()
}
