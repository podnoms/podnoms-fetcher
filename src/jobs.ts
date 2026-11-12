// A listing or download, tried again on a new IP when the platform refuses
// us. Each job's answer is a stream of messages (see app.ts).
import { randomUUID } from 'node:crypto'
import { extname, join } from 'node:path'
import { config } from './config'
import { ensureWorkDir, keepFile, outputTemplate, removeJobFiles } from './files'
import { log } from './log'
import { rotateFrom, rotationGeneration } from './rotate'
import { asJob } from './update'
import { downloadArgs, listArgs, runYtDlp } from './ytdlp'

export type Message =
  | { line: string }
  | { end: true }
  | { file: { id: string; ext: string } }
  | { error: string; rateLimited: boolean }

export type Send = (message: Message) => void

type Context = { send: Send; signal?: AbortSignal; job: Record<string, unknown> }

// Runs yt-dlp until it succeeds, or fails for any reason but a refusal we
// can get round with a new IP. Returns whether it succeeded; a failure has
// already been sent.
async function withRotation(args: string[], { send, signal, job }: Context, beforeRetry?: () => Promise<void>) {
  for (let attempt = 0; ; attempt++) {
    const seen = rotationGeneration()
    const result = await runYtDlp(args, (line) => send({ line }), signal)
    if (result.ok) return true
    const retry = result.rateLimited && !signal?.aborted && attempt < config.rotateRetries && (await rotateFrom(seen))
    if (!retry) {
      if (result.rateLimited) log.warn({ ...job, attempt, error: result.error }, 'Refused by the platform')
      send({ error: result.error, rateLimited: result.rateLimited })
      return false
    }
    log.info({ ...job, attempt }, 'Refused by the platform; trying again on a new IP')
    await beforeRetry?.()
  }
}

export function listJob(url: string, limit: number | null, context: Omit<Context, 'job'>) {
  return asJob(async () => {
    const job = { kind: 'list', url }
    if (await withRotation(listArgs(url, limit), { ...context, job })) context.send({ end: true })
  })
}

export function downloadJob(url: string, limitRate: string | undefined, context: Omit<Context, 'job'>) {
  return asJob(() => download(url, limitRate, context))
}

async function download(url: string, limitRate: string | undefined, context: Omit<Context, 'job'>) {
  const id = randomUUID()
  const job = { kind: 'download', url, id }
  const dir = await ensureWorkDir()
  let filepath: string | undefined
  const send: Send = (message) => {
    if ('line' in message && message.line.startsWith('INFO ')) {
      try {
        filepath = (JSON.parse(message.line.slice(5)) as { filepath?: string }).filepath
      } catch {}
    }
    context.send(message)
  }
  const started = Date.now()
  const ok = await withRotation(downloadArgs(url, outputTemplate(id), limitRate), { ...context, send, job }, () => removeJobFiles(id))
  if (!ok) return removeJobFiles(id)
  const ext = filepath ? extname(filepath).slice(1) : ''
  // Only a file of this job's, in the work folder.
  if (!filepath || !ext || filepath !== join(dir, `${id}.${ext}`)) {
    await removeJobFiles(id)
    return context.send({ error: "yt-dlp didn't say where it saved the download", rateLimited: false })
  }
  keepFile(id, filepath)
  log.info({ ...job, durationMs: Date.now() - started }, 'Downloaded')
  context.send({ file: { id, ext } })
}
