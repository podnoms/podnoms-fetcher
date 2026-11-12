import { existsSync, utimesSync } from 'node:fs'
import { readdir } from 'node:fs/promises'
import { afterEach, describe, expect, it } from 'vitest'
import { app } from '../src/app'
import { config } from '../src/config'
import { ensureWorkDir, sweep } from '../src/files'
import type { Message } from '../src/jobs'
import { startFakeGluetun } from './fixtures/fake-gluetun'

const auth = { Authorization: `Bearer ${config.secret}` }

const post = (path: string, body: unknown) =>
  app.request(path, { method: 'POST', headers: { ...auth, 'Content-Type': 'application/json' }, body: JSON.stringify(body) })

async function messages(response: Response) {
  expect(response.status).toBe(200)
  const text = await response.text()
  return text
    .split('\n')
    .filter(Boolean)
    .map((line) => JSON.parse(line) as Message)
}

const last = (list: Message[]) => list.at(-1)
const lines = (list: Message[]) => list.flatMap((m) => ('line' in m ? [m.line] : []))

let gluetun: Awaited<ReturnType<typeof startFakeGluetun>> | undefined
afterEach(async () => {
  await gluetun?.close()
  gluetun = undefined
})

async function withGluetun(options?: Parameters<typeof startFakeGluetun>[0]) {
  gluetun = await startFakeGluetun(options)
  config.gluetun = { url: gluetun.url, apiKey: gluetun.apiKey }
  return gluetun
}

describe('auth', () => {
  it('turns away requests without the secret', async () => {
    expect((await app.request('/health')).status).toBe(401)
    expect((await app.request('/health', { headers: { Authorization: 'Bearer wrong-secret-0123456' } })).status).toBe(401)
  })

  it('reports its health', async () => {
    const response = await app.request('/health', { headers: auth })
    expect(await response.json()).toEqual({ ytDlpVersion: '2026.08.19', publicIp: null })
  })
})

describe('POST /list', () => {
  it("streams the channel's entries, then ends", async () => {
    const result = await messages(await post('/list', { url: 'https://video.test/channel?entries=c,b,a', limit: 2 }))
    expect(lines(result).filter((l) => l.startsWith('ENTRY '))).toHaveLength(2)
    expect(lines(result).some((l) => l.startsWith('PLAYLIST '))).toBe(true)
    expect(last(result)).toEqual({ end: true })
  })

  it('rejects anything but a link and a limit', async () => {
    expect((await post('/list', { url: 'file:///etc/passwd', limit: 1 })).status).toBe(400)
    expect((await post('/list', { url: 'https://video.test/x' })).status).toBe(400)
  })

  it('passes on failures', async () => {
    const result = await messages(await post('/list', { url: 'https://video.test/fail', limit: null }))
    expect(last(result)).toEqual({ error: 'This video is unavailable', rateLimited: false })
  })
})

describe('POST /download', () => {
  it('streams progress, then the file to collect, which can be deleted', async () => {
    const result = await messages(await post('/download', { url: 'https://video.test/watch' }))
    expect(lines(result).filter((l) => l.startsWith('PROGRESS '))).toHaveLength(3)
    expect(lines(result).some((l) => l.startsWith('DETAILS '))).toBe(true)
    const end = last(result)
    expect(end).toMatchObject({ file: { ext: 'm4a' } })
    const { id } = (end as { file: { id: string } }).file

    const file = await app.request(`/files/${id}`, { headers: auth })
    expect(file.status).toBe(200)
    expect(await file.text()).toBe('fake audio')

    expect((await app.request(`/files/${id}`, { method: 'DELETE', headers: auth })).status).toBe(204)
    expect((await app.request(`/files/${id}`, { headers: auth })).status).toBe(404)
    expect(await readdir(config.workDir)).toEqual([])
  })

  it("fails if yt-dlp doesn't say where it saved the file, leaving nothing behind", async () => {
    const result = await messages(await post('/download', { url: 'https://video.test/no-info' }))
    expect(last(result)).toEqual({ error: "yt-dlp didn't say where it saved the download", rateLimited: false })
    expect(await readdir(config.workDir)).toEqual([])
  })

  it('only takes yt-dlp rate limits', async () => {
    expect((await post('/download', { url: 'https://video.test/watch', limitRate: '--exec rm' })).status).toBe(400)
  })

  it("won't serve files it didn't download", async () => {
    expect((await app.request('/files/..%2F..%2Fetc%2Fpasswd', { headers: auth })).status).toBe(404)
  })
})

describe('when the platform refuses us', () => {
  it('says so, without gluetun to change IP', async () => {
    const result = await messages(await post('/download', { url: 'https://video.test/rate-limited' }))
    expect(last(result)).toMatchObject({ rateLimited: true })
  })

  it('changes IP and tries again', async () => {
    const fake = await withGluetun()
    const result = await messages(await post('/download', { url: 'https://video.test/watch?refuse=10.0.0.1' }))
    expect(last(result)).toMatchObject({ file: { ext: 'm4a' } })
    expect(fake.state.stops).toBe(1)
    expect((await (await app.request('/health', { headers: auth })).json()) as object).toMatchObject({ publicIp: '10.0.0.2' })
  })

  it('gives up after its retries', async () => {
    const fake = await withGluetun()
    const result = await messages(await post('/list', { url: 'https://video.test/rate-limited', limit: 1 }))
    expect(last(result)).toMatchObject({ rateLimited: true })
    expect(fake.state.stops).toBe(config.rotateRetries)
  })

  it('changes IP once for refusals that come together', async () => {
    const fake = await withGluetun()
    const url = 'https://video.test/watch?refuse=10.0.0.1'
    const results = await Promise.all([1, 2, 3].map(() => Promise.resolve(post('/download', { url })).then(messages)))
    for (const result of results) expect(last(result)).toMatchObject({ file: { ext: 'm4a' } })
    expect(fake.state.stops).toBe(1)
  })

  it("doesn't change IP again so soon after the last time", async () => {
    const fake = await withGluetun()
    config.rotateMinIntervalMs = 60_000
    const result = await messages(await post('/list', { url: 'https://video.test/rate-limited', limit: 1 }))
    expect(last(result)).toMatchObject({ rateLimited: true })
    expect(fake.state.stops).toBe(1)
  })

  it("gives up if gluetun can't be told to reconnect", async () => {
    await withGluetun({ apiKey: 'other-key' })
    const result = await messages(await post('/list', { url: 'https://video.test/rate-limited', limit: 1 }))
    expect(last(result)).toMatchObject({ rateLimited: true })
  })
})

describe('sweep', () => {
  it('removes downloads nobody collected', async () => {
    const result = await messages(await post('/download', { url: 'https://video.test/watch' }))
    const { id, ext } = (last(result) as { file: { id: string; ext: string } }).file
    const path = `${await ensureWorkDir()}/${id}.${ext}`
    await sweep()
    expect(existsSync(path)).toBe(true)
    const old = new Date(Date.now() - config.fileTtlMs - 1000)
    utimesSync(path, old, old)
    await sweep()
    expect(existsSync(path)).toBe(false)
    expect((await app.request(`/files/${id}`, { headers: auth })).status).toBe(404)
  })
})
