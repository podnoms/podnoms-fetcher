// Each test file gets its own work folder and IP file, and starts with no
// rotation set up.
import { mkdtempSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { beforeEach } from 'vitest'
import { config } from '../src/config'
import { resetFiles } from '../src/files'
import { resetRotation } from '../src/rotate'

const dir = mkdtempSync(join(tmpdir(), 'podnoms-fetcher-test-'))
config.workDir = join(dir, 'work')
process.env.FAKE_IP_FILE = join(dir, 'ip')

beforeEach(() => {
  config.gluetun = null
  config.rotateRetries = 2
  config.rotateMinIntervalMs = 0
  config.rotatePollMs = 5
  config.rotateTimeoutMs = 2000
  config.ytDlpUpdate = { pip: null, intervalMs: 0 }
  resetRotation()
  resetFiles()
})
