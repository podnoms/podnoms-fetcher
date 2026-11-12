import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { config } from '../src/config'
import { asJob, updateYtDlp } from '../src/update'
import { forgetYtDlpVersion } from '../src/ytdlp'

const fakePip = fileURLToPath(new URL('./fixtures/fake-pip.mjs', import.meta.url))
let dir: string

beforeEach(() => {
  dir = mkdtempSync(join(tmpdir(), 'fetcher-update-'))
  config.ytDlpUpdate = { pip: fakePip, intervalMs: 0 }
  process.env.FAKE_PIP_VERSION_FILE = join(dir, 'version')
  forgetYtDlpVersion()
})
afterEach(() => {
  for (const name of ['FAKE_PIP_VERSION_FILE', 'FAKE_PIP_NEW_VERSION', 'FAKE_PIP_FAIL', 'FAKE_PIP_HOLD_MS']) delete process.env[name]
  forgetYtDlpVersion()
  rmSync(dir, { recursive: true, force: true })
})

// A job that runs until told to finish.
function heldJob() {
  let finish = () => {}
  const done = asJob(() => new Promise<void>((resolve) => (finish = resolve)))
  return { done, finish: () => finish() }
}

describe('updateYtDlp', () => {
  it('does nothing without YTDLP_PIP', async () => {
    config.ytDlpUpdate = { pip: null, intervalMs: 0 }
    expect(await updateYtDlp()).toEqual({ skipped: 'not configured' })
  })

  it('upgrades yt-dlp and reports the versions', async () => {
    process.env.FAKE_PIP_NEW_VERSION = '2026.11.08'
    expect(await updateYtDlp()).toEqual({ from: '2026.08.19', to: '2026.11.08', updated: true })
  })

  it('says so when it was already the latest', async () => {
    expect(await updateYtDlp()).toEqual({ from: '2026.08.19', to: '2026.08.19', updated: false })
  })

  it('waits for another time while a job is running', async () => {
    const job = heldJob()
    await Promise.resolve()
    expect(await updateYtDlp()).toEqual({ skipped: 'jobs running' })
    job.finish()
    await job.done
  })

  it("holds new jobs until it's done", async () => {
    process.env.FAKE_PIP_HOLD_MS = '200'
    const update = updateYtDlp()
    let started = false
    const job = asJob(async () => {
      started = true
    })
    await new Promise((resolve) => setTimeout(resolve, 50))
    expect(started).toBe(false)
    await update
    await job
    expect(started).toBe(true)
  })

  it('fails when pip does, letting jobs run again', async () => {
    process.env.FAKE_PIP_FAIL = '1'
    await expect(updateYtDlp()).rejects.toThrow()
    expect(await asJob(async () => 'ran')).toBe('ran')
  })
})
