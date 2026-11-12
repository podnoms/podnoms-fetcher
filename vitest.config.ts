import { fileURLToPath } from 'node:url'
import { defineConfig } from 'vitest/config'

export default defineConfig({
  test: {
    include: ['tests/**/*.test.ts'],
    setupFiles: ['tests/setup.ts'],
    env: {
      FETCHER_SECRET: 'test-secret-0123456789',
      YTDLP_PATH: fileURLToPath(new URL('./tests/fixtures/fake-yt-dlp.mjs', import.meta.url)),
    },
  },
})
