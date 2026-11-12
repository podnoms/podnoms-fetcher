#!/usr/bin/env node
// Stands in for yt-dlp's pip in tests: "upgrades" yt-dlp by writing
// FAKE_PIP_NEW_VERSION to FAKE_PIP_VERSION_FILE, which fake-yt-dlp.mjs's
// --version reports, after waiting FAKE_PIP_HOLD_MS. Fails when FAKE_PIP_FAIL is set.
import { writeFileSync } from 'node:fs'

if (process.env.FAKE_PIP_HOLD_MS) await new Promise((resolve) => setTimeout(resolve, Number(process.env.FAKE_PIP_HOLD_MS)))
if (process.env.FAKE_PIP_FAIL) {
  console.error('ERROR: Could not install packages')
  process.exit(1)
}
if (process.env.FAKE_PIP_VERSION_FILE && process.env.FAKE_PIP_NEW_VERSION) {
  writeFileSync(process.env.FAKE_PIP_VERSION_FILE, process.env.FAKE_PIP_NEW_VERSION)
}
