// Run with Bun: `bun src/server.ts`.
import { app } from './app'
import { config } from './config'
import { ensureWorkDir, sweep } from './files'
import { log } from './log'
import { publicIp } from './rotate'
import { scheduleUpdates } from './update'
import { ytDlpVersion } from './ytdlp'

await ensureWorkDir()
scheduleUpdates()
setInterval(() => void sweep().catch((error: unknown) => log.error({ err: error }, 'Could not sweep downloads')), 10 * 60_000)

log.info(
  { port: config.port, ytDlpVersion: await ytDlpVersion(), publicIp: await publicIp(), rotation: Boolean(config.gluetun) },
  'Listening',
)

export default {
  port: config.port,
  fetch: app.fetch,
  // Downloads can run for a long time between progress lines; never time
  // a connection out for being quiet.
  idleTimeout: 0,
}
