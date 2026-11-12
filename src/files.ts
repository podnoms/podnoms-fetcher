// Finished downloads, kept until PodNoms collects (and deletes) them, and
// swept away if it never does, along with anything left over from failed runs.
import { mkdir, readdir, rm, stat } from 'node:fs/promises'
import { basename, join } from 'node:path'
import { config } from './config'
import { log } from './log'

const files = new Map<string, string>()

export async function ensureWorkDir() {
  await mkdir(config.workDir, { recursive: true })
  return config.workDir
}

// Every file a download writes starts with its id.
export const outputTemplate = (id: string) => join(config.workDir, `${id}.%(ext)s`)

export function keepFile(id: string, path: string) {
  files.set(id, path)
}

export function filePath(id: string) {
  return files.get(id) ?? null
}

export async function removeFile(id: string) {
  const path = files.get(id)
  files.delete(id)
  if (path) await rm(path, { force: true })
  return Boolean(path)
}

// Removes the rest of a job's files (partial downloads and the like).
export async function removeJobFiles(id: string) {
  const names = await readdir(config.workDir).catch(() => [])
  await Promise.all(names.filter((name) => name.startsWith(`${id}.`)).map((name) => rm(join(config.workDir, name), { force: true })))
}

export async function sweep(now = Date.now()) {
  const names = await readdir(config.workDir).catch(() => [])
  for (const name of names) {
    const path = join(config.workDir, name)
    const info = await stat(path).catch(() => null)
    if (!info || now - info.mtimeMs < config.fileTtlMs) continue
    await rm(path, { recursive: true, force: true })
    const id = basename(name).split('.')[0]!
    if (files.get(id) === path) files.delete(id)
    log.info({ file: name }, 'Removed an uncollected download')
  }
}

export function resetFiles() {
  files.clear()
}
