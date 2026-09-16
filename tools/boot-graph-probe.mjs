/**
 * Reads a running harness server's boot graph and the plugin bundle it serves.
 *
 * The harness hands the browser its plugins through `window.__DSH_BOOT__`, which
 * the web server injects into the page. Everything between "the plugin is
 * installed in this profile" and "the plugin is running in the page" happens in
 * that graph, and none of it is visible to the app: a row the loader never
 * emits, or a bundle route that answers 404, looks from the outside exactly
 * like a UI that has nothing to show.
 *
 * So this asks the server itself. It mints the browser cookie the root token
 * issues, reads the graph out of the served HTML, fetches the plugin's own
 * bundle, and checks the source the browser would run for the facts the plugin
 * depends on - its module id, its marker, and the ids its list slots require.
 *
 * Usage: node tools/boot-graph-probe.mjs <base-url> <token>
 *   <base-url>  e.g. http://127.0.0.1:62639
 *   <token>     the token from the `dsh web:` ready line
 * Exit codes: 0 the plugin is served intact, 1 it is not, 2 the arguments are wrong.
 */

const [base, token] = process.argv.slice(2)

let passed = 0
let failed = 0

function check(ok, what) {
  if (ok) {
    passed++
    console.log('  PASS  ' + what)
  } else {
    failed++
    console.log('  FAIL  ' + what)
  }
}

/**
 * Reads the served graph and the plugin bundle.
 * @returns the exit code to use.
 */
async function main() {
  if (!base || !token) {
    console.error('usage: node tools/boot-graph-probe.mjs <base-url> <token>')
    return 2
  }

  console.log('boot graph probe: ' + base)

  // The root token mints a browser cookie and redirects to a clean `/`. A plain
  // fetch does not carry cookies across redirects, so the redirect is followed
  // by hand.
  const minted = await fetch(`${base}/?token=${encodeURIComponent(token)}`, { redirect: 'manual' })
  const setCookie = minted.headers.getSetCookie?.() ?? []
  const cookie = setCookie.map((entry) => entry.split(';')[0]).join('; ')
  check(minted.status === 303 && cookie.length > 0, 'the ready-line token mints a browser session')
  if (!cookie) return 1

  const html = await (await fetch(`${base}/`, { headers: { cookie } })).text()
  const graphText = html.match(/globalThis\["__DSH_BOOT__"\] = ([\s\S]*?)<\/script>/)
  check(Boolean(graphText), 'the served page carries a boot graph')
  if (!graphText) return 1

  const graph = JSON.parse(graphText[1])
  const entries = graph.entries ?? []
  const row = entries.find((entry) => entry.id === 'dsh-plugin-desktop-updates')
  check(Boolean(row), `the graph has a row for dsh-plugin-desktop-updates (${entries.length} rows in total)`)
  if (!row) return 1

  console.log('  row url: ' + row.url)
  const response = await fetch(new URL(row.url, base), { headers: { cookie } })
  // The checkout may be CRLF, so compare with normalized line endings.
  const body = (await response.text()).replace(/\r\n/g, '\n')
  check(response.status === 200, `the bundle route answers 200 (${response.status}, ${body.length} bytes)`)

  const facts = [
    ['the module id the row asks for', "id: 'dsh-plugin-desktop-updates'"],
    ['the sidebar entry', "'sidebar.footer.action'"],
    ['the id a list slot requires', "name: 'sidebar.footer.action',\n\t\t\t\t\tid: 'desktop-updates'"],
    ['the settings section', "'settings.section'"],
    ['the marker the app reads back', '__dshDesktopUpdates'],
    ['the report of a contribution the shell refused', 'marker.failed.push(slot'],
  ]
  for (const [what, needle] of facts) {
    check(body.includes(needle), 'the served bundle carries ' + what)
  }

  return failed === 0 ? 0 : 1
}

// The code goes to process.exitCode rather than process.exit(): an exit while
// the fetch client still holds sockets trips a libuv assertion on Windows that
// would replace this probe's exit code with 1.
process.exitCode = await main()
console.log(`${passed} passed, ${failed} failed`)
