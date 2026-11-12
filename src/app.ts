// The HTTP API PodNoms calls. Jobs answer with newline-delimited JSON: a
// message per yt-dlp output line ({ line }), then exactly one of { end },
// { file } (a download, to collect from /files/:id) or { error, rateLimited }.
import { createReadStream } from 'node:fs'
import { stat } from 'node:fs/promises'
import { Readable } from 'node:stream'
import { Hono } from 'hono'
import { bearerAuth } from 'hono/bearer-auth'
import { z } from 'zod'
import { config } from './config'
import { filePath, removeFile } from './files'
import { downloadJob, listJob, type Send } from './jobs'
import { log } from './log'
import { publicIp } from './rotate'
import { ytDlpVersion } from './ytdlp'

const link = z
  .string()
  .url()
  .refine((url) => /^https?:$/.test(new URL(url).protocol), 'Only http and https links')

const listBody = z.object({ url: link, limit: z.number().int().positive().nullable() })
// yt-dlp's --limit-rate, e.g. 2M or 500K.
const downloadBody = z.object({ url: link, limitRate: z.string().regex(/^\d+(\.\d+)?[KMG]?$/i).optional() })
const fileId = z.string().uuid()

function stream(signal: AbortSignal, run: (send: Send) => Promise<void>) {
  const encoder = new TextEncoder()
  const body = new ReadableStream<Uint8Array>({
    async start(controller) {
      const send: Send = (message) => {
        try {
          controller.enqueue(encoder.encode(`${JSON.stringify(message)}\n`))
        } catch {
          // The caller has gone; the job is stopped by the signal.
        }
      }
      try {
        await run(send)
      } catch (error) {
        log.error({ err: error }, 'Job failed')
        send({ error: error instanceof Error ? error.message : String(error), rateLimited: false })
      } finally {
        try {
          controller.close()
        } catch {}
      }
    },
  })
  return new Response(body, { headers: { 'Content-Type': 'application/x-ndjson', 'Cache-Control': 'no-store' } })
}

export const app = new Hono()

app.use('*', (c, next) => bearerAuth({ token: config.secret })(c, next))

app.get('/health', async (c) => c.json({ ytDlpVersion: await ytDlpVersion(), publicIp: await publicIp() }))

app.post('/list', async (c) => {
  const body = listBody.safeParse(await c.req.json().catch(() => null))
  if (!body.success) return c.json({ error: 'Expected { url, limit }' }, 400)
  const { signal } = c.req.raw
  return stream(signal, (send) => listJob(body.data.url, body.data.limit, { send, signal }))
})

app.post('/download', async (c) => {
  const body = downloadBody.safeParse(await c.req.json().catch(() => null))
  if (!body.success) return c.json({ error: 'Expected { url, limitRate? }' }, 400)
  const { signal } = c.req.raw
  return stream(signal, (send) => downloadJob(body.data.url, body.data.limitRate, { send, signal }))
})

app.get('/files/:id', async (c) => {
  const id = fileId.safeParse(c.req.param('id'))
  const path = id.success ? filePath(id.data) : null
  const info = path ? await stat(path).catch(() => null) : null
  if (!path || !info) return c.json({ error: 'No such file' }, 404)
  const body = Readable.toWeb(createReadStream(path)) as ReadableStream<Uint8Array>
  return new Response(body, { headers: { 'Content-Type': 'application/octet-stream', 'Content-Length': String(info.size) } })
})

app.delete('/files/:id', async (c) => {
  const id = fileId.safeParse(c.req.param('id'))
  if (!id.success || !(await removeFile(id.data))) return c.json({ error: 'No such file' }, 404)
  return c.body(null, 204)
})
