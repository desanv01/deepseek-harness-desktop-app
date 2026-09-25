/**
 * Writes the Harness's own runtime warnings and errors to stderr, where the
 * desktop app already records everything the child prints.
 *
 * Cordis's logger only keeps an in-memory ring of recent messages, and nothing
 * in the shipped composition exports them anywhere. So an error that happens
 * after startup - a session that cannot activate, a preset that fails to mount -
 * left no trace once the process was gone, and the app could only report that
 * the boot failed, not why. The default exporter level also filters out `warn`,
 * so warnings were not even in the ring.
 *
 * Session activation failures never reach the logger at all: the session
 * controller pushes them to the client as `api-session/error`. Those are
 * recorded here too.
 *
 * Every line carries LINE_PREFIX. The app's loader-failure parsers skip such
 * lines, so bridged runtime chatter can never change which plugin a recovery
 * pass blames or disables.
 *
 * This module deliberately imports nothing. Cordis reaches it only through
 * `ctx`, so the plugin has no module-scope dependency to resolve - which is what
 * lets its formatting and rate-limiting be exercised without a harness.
 */
export const name = 'dsh-plugin-log-bridge'

/** Marks every bridged line; the app's failure parser skips lines with it. */
export const LINE_PREFIX = '[harness-log]'

/** Written once per launch, so a log without it can be read as predating the bridge. */
export const BRIDGE_READY_LINE = `${LINE_PREFIX} info dsh-plugin-log-bridge: runtime warnings and errors are recorded from here on`

/** Cordis levels: error 0, info 1, warn 2. Admit up to warn. */
const EXPORT_LEVEL = 2
/** Lines written per window; an error in a loop must not flood the log. */
const WINDOW_MS = 60_000
const WINDOW_LIMIT = 100

/**
 * Renders one log record as text. Cordis's own formatter is not used so that
 * this module needs no import; the record's `args` are already the values the
 * caller logged.
 * @param {{ name?: string, args?: unknown[] }} message - a Cordis log record.
 * @returns {string} the message text, possibly spanning lines.
 */
export function formatMessage(message) {
  const args = Array.isArray(message?.args) ? message.args : []
  return args
    .map((value) => {
      if (typeof value === 'string') return value
      if (value instanceof Error) return value.stack ?? String(value)
      try { return JSON.stringify(value) } catch { return String(value) }
    })
    .join(' ')
}

/**
 * Renders one message as prefixed lines.
 * @param {string} type - `error`, `warn`, or `session-error` for activation failures.
 * @param {string} source - the logger name, or the session id.
 * @param {unknown} text - the message, possibly spanning lines (a stack trace).
 * @returns {string[]} the lines to write, each carrying the prefix.
 */
export function bridgeLines(type, source, text) {
  const [first = '', ...rest] = String(text).split(/\r?\n/)
  return [
    `${LINE_PREFIX} ${type} ${source}: ${first}`,
    ...rest.filter((line) => line.trim() !== '').map((line) => `${LINE_PREFIX}   ${line}`),
  ]
}

/**
 * A writer that drops what exceeds the window's budget, and says how much it
 * dropped once the next window opens.
 * @param {(text: string) => void} write - the sink.
 * @param {() => number} now - clock, injectable so the budget can be tested.
 * @returns {(lines: string[]) => void} one write per message.
 */
export function createLimitedWriter(write, now = Date.now) {
  let windowStart = now()
  let written = 0
  let dropped = 0
  return (lines) => {
    const time = now()
    if (time - windowStart >= WINDOW_MS) {
      if (dropped > 0) {
        write(`${LINE_PREFIX} warn dsh-plugin-log-bridge: dropped ${dropped} message(s) over the rate limit\n`)
      }
      windowStart = time
      written = 0
      dropped = 0
    }
    if (written >= WINDOW_LIMIT) {
      dropped += 1
      return
    }
    written += 1
    write(`${lines.join('\n')}\n`)
  }
}

/**
 * Attaches the bridge. Every step is wrapped so that a failure here cannot take
 * down the boot it exists to explain: a log bridge that can break startup is
 * worse than no bridge at all.
 * @param {object} ctx - the Cordis context.
 */
export function apply(ctx) {
  const emit = createLimitedWriter((text) => {
    try { process.stderr.write(text) } catch { /* the app may have closed the pipe */ }
  })

  const exporter = {
    colors: 0,
    levels: { default: EXPORT_LEVEL },
    export(message) {
      if (message?.type !== 'error' && message?.type !== 'warn') return
      emit(bridgeLines(message.type, String(message.name ?? 'harness'), formatMessage(message)))
    },
  }

  try {
    // Errors logged before this plugin loaded are still in the ring; warnings
    // were filtered out of it by the default level and are already gone.
    const buffered = ctx?.logger?.buffer
    if (Array.isArray(buffered)) {
      for (const message of buffered) {
        if (message?.type === 'error') exporter.export(message)
      }
    }
    ctx?.logger?.exporter(exporter)
  } catch {
    // No logger to attach to: the app still gets stdout and stderr as before.
  }

  try {
    ctx?.on?.('api-session/error', (sessionId, error) => {
      emit(bridgeLines('session-error', String(sessionId), error))
    })
  } catch { }

  // One line per launch. A notice rather than a problem, so it goes to stdout;
  // stderr carries only warnings and errors.
  try { process.stdout.write(`${BRIDGE_READY_LINE}\n`) } catch { }
}
