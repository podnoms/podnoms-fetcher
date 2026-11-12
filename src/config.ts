// Settings, from the environment. Tests change them in place.
import { z } from 'zod'

const optional = z
  .string()
  .optional()
  .transform((value) => value || undefined)

const schema = z.object({
  // Shared with PodNoms (its FETCHER_SECRET); every request must carry it.
  FETCHER_SECRET: z.string().min(16, 'FETCHER_SECRET must be at least 16 characters'),
  PORT: z.coerce.number().int().positive().default(8080),
  YTDLP_PATH: z.string().default('yt-dlp'),
  // Where downloads are kept until PodNoms collects them.
  WORK_DIR: z.string().default('/tmp/podnoms-fetcher'),
  // Gluetun's control server, e.g. http://localhost:8000. Without it, a
  // refusal is passed straight back to PodNoms.
  GLUETUN_CONTROL_URL: optional,
  GLUETUN_API_KEY: optional,
  // How many new IPs one job may try before giving up.
  ROTATE_RETRIES: z.coerce.number().int().min(0).default(2),
  // A refusal this soon after the last rotation isn't worth another.
  ROTATE_MIN_INTERVAL_SECONDS: z.coerce.number().min(0).default(120),
  // How long to wait for gluetun to come back with a new IP.
  ROTATE_TIMEOUT_SECONDS: z.coerce.number().positive().default(90),
  // pip in yt-dlp's virtualenv (set in the Docker image). With it, yt-dlp is
  // upgraded this often while running, besides on start; 0 turns that off.
  YTDLP_PIP: optional,
  YTDLP_UPDATE_INTERVAL_HOURS: z.coerce.number().min(0).default(4),
})

function load() {
  const env = schema.parse(process.env)
  return {
    secret: env.FETCHER_SECRET,
    port: env.PORT,
    ytDlpPath: env.YTDLP_PATH,
    workDir: env.WORK_DIR,
    gluetun: env.GLUETUN_CONTROL_URL ? { url: env.GLUETUN_CONTROL_URL.replace(/\/$/, ''), apiKey: env.GLUETUN_API_KEY } : null,
    rotateRetries: env.ROTATE_RETRIES,
    rotateMinIntervalMs: env.ROTATE_MIN_INTERVAL_SECONDS * 1000,
    rotateTimeoutMs: env.ROTATE_TIMEOUT_SECONDS * 1000,
    ytDlpUpdate: { pip: env.YTDLP_PIP ?? null, intervalMs: env.YTDLP_UPDATE_INTERVAL_HOURS * 60 * 60_000 },
    // How often to ask gluetun whether the new IP is up.
    rotatePollMs: 2000,
    // Downloads nobody has collected are removed after this.
    fileTtlMs: 60 * 60_000,
  }
}

export type Config = ReturnType<typeof load>

export const config: Config = load()
