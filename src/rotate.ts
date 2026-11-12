// Changes our public IP by having gluetun reconnect, which picks a new
// server from its configured list. Only one rotation happens at a time, and
// each one is numbered (its generation): a job refused on an IP we've since
// moved off just tries again, rather than asking for another rotation.
import { config } from './config'
import { log } from './log'

let generation = 0
let lastRotatedAt = 0
let inFlight: Promise<boolean> | null = null

export function rotationGeneration() {
  return generation
}

async function gluetun(path: string, init?: RequestInit) {
  const { url, apiKey } = config.gluetun!
  const response = await fetch(`${url}${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...(apiKey ? { 'X-API-Key': apiKey } : {}), ...init?.headers },
    signal: AbortSignal.timeout(15_000),
  })
  if (!response.ok) throw new Error(`gluetun answered ${response.status} to ${init?.method ?? 'GET'} ${path}`)
  return response
}

// Our public IP as gluetun sees it, or null if it isn't known (yet).
export async function publicIp() {
  if (!config.gluetun) return null
  try {
    const { public_ip } = (await (await gluetun('/v1/publicip/ip')).json()) as { public_ip?: string }
    return public_ip || null
  } catch {
    return null
  }
}

const setStatus = (status: 'stopped' | 'running') => gluetun('/v1/vpn/status', { method: 'PUT', body: JSON.stringify({ status }) })

async function reconnect() {
  const before = await publicIp()
  await setStatus('stopped')
  await setStatus('running')
  const deadline = Date.now() + config.rotateTimeoutMs
  while (Date.now() < deadline) {
    await new Promise((resolve) => setTimeout(resolve, config.rotatePollMs))
    const after = await publicIp()
    if (after && after !== before) {
      log.info({ before, after }, 'Changed public IP')
      return true
    }
  }
  log.warn({ before }, 'Gluetun reconnected but the public IP did not change')
  return false
}

// For a job that was refused while `seen` was the generation: whether it's
// worth trying again on a new IP. Rotates unless that's already happened
// since, or happened too recently to try again.
export async function rotateFrom(seen: number): Promise<boolean> {
  if (!config.gluetun) return false
  if (generation !== seen) return true
  if (inFlight) return inFlight
  if (Date.now() - lastRotatedAt < config.rotateMinIntervalMs) return false
  inFlight = reconnect()
    .catch((error: unknown) => {
      log.error({ err: error }, 'Could not change public IP')
      return false
    })
    .then((changed) => {
      lastRotatedAt = Date.now()
      if (changed) generation++
      return changed
    })
    .finally(() => {
      inFlight = null
    })
  return inFlight
}

// Forgets everything; for tests.
export function resetRotation() {
  generation = 0
  lastRotatedAt = 0
  inFlight = null
}
