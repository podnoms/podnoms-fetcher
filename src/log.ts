// One JSON object per line on stdout, as PodNoms logs in production.
type Level = 'info' | 'warn' | 'error'

function write(level: Level, fields: Record<string, unknown>, msg: string) {
  if (process.env.VITEST) return
  const err = fields.err instanceof Error ? { type: fields.err.name, message: fields.err.message } : fields.err
  console.log(JSON.stringify({ level, time: new Date().toISOString(), app: 'podnoms-fetcher', ...fields, ...(err ? { err } : {}), msg }))
}

export const log = {
  info: (fields: Record<string, unknown>, msg: string) => write('info', fields, msg),
  warn: (fields: Record<string, unknown>, msg: string) => write('warn', fields, msg),
  error: (fields: Record<string, unknown>, msg: string) => write('error', fields, msg),
}
