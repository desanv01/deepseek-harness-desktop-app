/**
 * Headless smoke test for the updates plugin's browser half.
 *
 * The plugin is a hand-written module-face bundle: the shell's loader hands it a
 * `require` over the frozen module table, and it returns a Cordis plugin whose
 * `apply` registers two slots. None of that needs a browser - only a `window`
 * with `__ModuleLoader__`, a module table with React in it, and a slot registry.
 *
 * This exists because WebView2 cannot start in every build environment, so the
 * UI code would otherwise be shipped on the strength of "the bundle is served"
 * alone. It renders the components as plain functions and asserts what they
 * produce: the sidebar entry, the settings section, the bridge-absent message,
 * and the live-state rows.
 *
 * Usage: node tools/client-plugin-smoke.mjs [path/to/client.js]
 */

import { readFileSync, existsSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import vm from 'node:vm'

const here = dirname(fileURLToPath(import.meta.url))
const clientPath = process.argv[2] ?? resolve(here, '../plugins/dsh-plugin-desktop-updates/client.js')

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

if (!existsSync(clientPath)) {
  console.error('client bundle not found: ' + clientPath)
  process.exit(2)
}

// ---- a React small enough to render the plugin headlessly -------------------

/**
 * One component render at a time. Hooks read and write the current frame, and
 * entering a component pushes a fresh one, so a component's state is its own -
 * and a re-render starts clean, like a fresh mount.
 */
let frame = null

function createElement(type, props, ...children) {
  // children may arrive as props (h(Button, { children: 'Check now' })) or
  // positionally; only positional ones replace what props already carries, the
  // way React treats them.
  const next = { ...(props ?? {}) }
  if (children.length > 0) next.children = children.length === 1 ? children[0] : children
  return { type, props: next }
}

function useState(initial) {
  // The setter belongs to the component that called it, not to whichever frame
  // is current: a click handler runs long after the render finished.
  const owner = frame
  const index = owner.index++
  if (owner.states[index] === undefined) {
    owner.states[index] = typeof initial === 'function' ? initial() : initial
  }
  const value = owner.states[index]
  return [value, (next) => { owner.states[index] = typeof next === 'function' ? next(value) : next }]
}

const React = {
  createElement,
  useState,
  useEffect: (fn) => { frame.effects.push(fn) },
  useCallback: (fn) => fn,
  useMemo: (fn) => fn(),
  Fragment: 'Fragment',
}

/** Renders a function component and expands the result. */
function renderComponent(component, props) {
  const previous = frame
  frame = { states: [], index: 0, effects: [] }
  try {
    const tree = component(props ?? {})
    // Effects run, as they would after a commit. Their cleanups are not run:
    // the component is not being unmounted, and running them here would undo
    // exactly what the effect installed (a subscription, say).
    for (const effect of frame.effects) effect()
    return expand(tree)
  } finally {
    frame = previous
  }
}

/** Replaces function components with what they render, recursively. */
function expand(node) {
  if (Array.isArray(node)) return node.map(expand)
  if (!node || typeof node !== 'object') return node
  if (node.props && typeof node.type === 'function') return renderComponent(node.type, node.props)
  if (node.props) {
    return { type: node.type, props: { ...node.props, children: expand(node.props.children) } }
  }
  return node
}

function render(component, props) {
  return renderComponent(component, props)
}

/** All text in a rendered tree, flattened. */
function textOf(node) {
  if (node === null || node === undefined || node === false) return ''
  if (typeof node === 'string' || typeof node === 'number') return String(node)
  if (Array.isArray(node)) return node.map(textOf).join('')
  if (typeof node === 'object' && node.props) return textOf(node.props.children)
  return ''
}

/** Depth-first search for the first element whose text matches. */
function findByText(node, needle) {
  if (!node || typeof node !== 'object') return null
  if (Array.isArray(node)) {
    for (const child of node) {
      const found = findByText(child, needle)
      if (found) return found
    }
    return null
  }
  if (typeof node === 'object' && node.props) {
    if (textOf(node).includes(needle)) return node
    const children = Array.isArray(node.props.children) ? node.props.children : [node.props.children]
    for (const child of children) {
      const found = findByText(child, needle)
      if (found) return found
    }
  }
  return null
}

/** Visits every element in a rendered tree. */
function walk(node, visit) {
  if (!node || typeof node !== 'object') return
  if (Array.isArray(node)) {
    for (const child of node) walk(child, visit)
    return
  }
  if (typeof node === 'object' && node.props) {
    visit(node)
    const children = Array.isArray(node.props.children) ? node.props.children : [node.props.children]
    for (const child of children) walk(child, visit)
  }
}

/**
 * The element a label belongs to: the innermost one that both shows the text and
 * can be clicked. Searching by text alone finds the container that wraps it.
 */
function findAction(node, needle) {
  let found = null
  walk(node, (element) => {
    if (found) return
    if (typeof element.props?.onClick === 'function' && textOf(element).includes(needle)) {
      found = element
    }
  })
  return found
}

// ---- a shell small enough to load the bundle --------------------------------

const posted = []
let stateListener = null
let progressListener = null

/**
 * The messages asking the app to open its native updates window, whichever
 * shape they came in: the stub bridge records a marker object, while a plugin
 * that goes through the page bridge's own client would post a request.
 */
function openRequests() {
  return posted.filter((message) =>
    message?.type === 'open' || (message?.dsh === 1 && message?.method === 'open'))
}

const bridge = {
  version: 1,
  appVersion: 'test',
  state: () => bridgeState,
  check: async () => bridgeState,
  install: async () => ({ ok: true, version: '2026.10.01' }),
  apply: async () => ({ ok: true }),
  harnessCheck: async () => bridgeState,
  harnessInstall: async () => ({ ok: true }),
  setPolicy: async () => ({ ok: true }),
  setPluginEnabled: async () => ({ ok: true }),
  installPlugin: async () => ({ ok: true }),
  removePlugin: async () => ({ ok: true }),
  openNative: async () => { posted.push({ type: 'open' }); return { ok: true } },
  subscribe: (listener) => { stateListener = listener; return () => { stateListener = null } },
  onProgress: (listener) => { progressListener = listener; return () => { progressListener = null } },
}

let bridgeState = {
  app: { version: '2026.09.16', latest: '2026.10.01', available: true, status: '2026.10.01 is available.', notes: '## Notes\n\n- one' },  harness: { installed: '0.1.5-rc.1', latest: '0.1.5-rc.1', alpha: '0.1.6-alpha.1', available: null, status: 'up to date' },
  staged: null,
  policy: { checkOnLaunch: true },
  plugins: {
    canManage: true,
    entries: [
      { name: '@deepseek-ai/dsh-base', version: '0.1.5', enabled: true, builtIn: true },
      { name: 'dsh-find-plugin', version: '0.3.7', enabled: false, builtIn: false },
    ],
  },
}

const registrations = []

/**
 * The slots the shipped shell declares, with the kind that governs registration.
 * Both list slots need an `id` and the keyed panel slot needs a `key`, exactly
 * as the real registry enforces.
 */
const declaredSlots = {
  'settings.section': 'list',
  'sidebar.panellist': 'list',
  main: 'keyed',
}

/** Declarations arriving late: inject waits, and the factory runs on declare. */
const waiting = []

/** Panel selections the plugin asked the layout service for. */
const selectedPanels = []

/** Whether the shell's layout service exists at all (a shell without it). */
let layoutAvailable = true

const context = {
  slots: {
    register: (descriptor, component) => {
      const kind = declaredSlots[descriptor?.name]
      if (kind === undefined) {
        throw new Error(`slot "${descriptor?.name}" is not declared`)
      }
      if (kind === 'list' && descriptor.id === undefined) {
        throw new Error(`list slot "${descriptor.name}" requires options.id`)
      }
      if (kind === 'keyed' && descriptor.key === undefined) {
        throw new Error(`keyed slot "${descriptor.name}" requires options.key`)
      }
      registrations.push({ ...descriptor, component })
      return () => {}
    },
    inject: (name, factory) => {
      // The shell's declaration may land after the plugin applies; the real
      // inject waits for it instead of throwing, so a callback failure surfaces
      // later, where a bare try/catch around apply() would not see it.
      if (declaredSlots[name] === undefined) {
        waiting.push({ name, factory })
        return () => {}
      }
      factory()
      return () => {}
    },
  },
  layout: {
    // The real service throws for an id no panel registered, and the plugin
    // leans on that being an error rather than a silent no-op.
    selectPanel: (id) => {
      if (!layoutAvailable) throw new Error('layout.selectPanel is unavailable')
      if (!registrations.some((entry) => entry.name === 'main' && entry.key === id)) {
        throw new Error(`layout.selectPanel: main panel "${id}" is not registered`)
      }
      selectedPanels.push(id)
    },
  },
}

/** Declares a slot the way the shell does, running whatever waited for it. */
function declareSlot(name, kind) {
  declaredSlots[name] = kind
  for (const pending of waiting.splice(0)) {
    if (pending.name === name) pending.factory()
  }
}

/**
 * The props a shell hands a registered component: the inject face the register
 * call declared, plus the owner props for that slot.
 */
function propsFor(descriptor, ownerProps) {
  const face = typeof descriptor?.inject === 'function' ? descriptor.inject() : {}
  return { ...face, ...(ownerProps || {}) }
}

const moduleTable = { react: React }
const window = {
  __ModuleLoader__: { load: (bundle) => { loaded = bundle } },
  chrome: { webview: { postMessage: (message) => posted.push(JSON.parse(message)), addEventListener: () => {} } },
  __dshDesktop: bridge,
}
let loaded = null

const sandbox = {
  window,
  document: { getElementById: () => null, createElement: () => ({ style: {}, addEventListener: () => {} }) },
  console,
  setTimeout,
  clearTimeout,
}
sandbox.globalThis = sandbox

console.log('client plugin smoke: ' + clientPath)

// ---- run it -----------------------------------------------------------------

vm.createContext(sandbox)
vm.runInContext(readFileSync(clientPath, 'utf8'), sandbox, { filename: clientPath })

check(loaded !== null, 'the bundle registers itself with the module loader')
check(loaded?.id === 'dsh-plugin-desktop-updates', 'it registers under its package id')

const factory = loaded?.factory
check(typeof factory === 'function', 'the registered bundle exposes a factory')

const exportsObject = factory((specifier) => {
  if (!(specifier in moduleTable)) throw new Error('undeclared module requested: ' + specifier)
  return moduleTable[specifier]
})

check(exportsObject.name === 'dsh-plugin-desktop-updates', 'the plugin exports its name')
check(Array.isArray(exportsObject.inject) && exportsObject.inject.includes('slots'), 'it injects the slot service')
check(
  Array.isArray(exportsObject.inject) && exportsObject.inject.includes('layout'),
  'it injects the layout service that opens its panel',
)
check(typeof exportsObject.apply === 'function', 'it exports apply()')

exportsObject.apply(context)

const section = registrations.find((entry) => entry.name === 'settings.section')
check(Boolean(section), 'apply() registers a settings section')
check(section?.id === 'desktop-updates', 'the section has a stable id')
check(typeof section?.label === 'function' && section.label() === 'Updates', 'the section is labelled Updates')
check(typeof section?.order === 'number', 'the section declares an order')

// The sidebar entry waits for the sidebar to declare its slot, exactly as it
// does when the shell mounts after the plugin applies - and that is where a
// registration the registry rejects surfaces, not in apply() itself.
let lateFailure = null
try {
  declareSlot('sidebar.footer.action', 'list')
} catch (error) {
  lateFailure = error
}
check(lateFailure === null, 'the sidebar entry registers when the sidebar declares the slot')

const footer = registrations.find((entry) => entry.name === 'sidebar.footer.action')
check(Boolean(footer), 'apply() contributes the sidebar entry beside Settings')
check(footer?.id === 'desktop-updates', 'the sidebar entry carries the list slot id the registry requires')

// The panel: a sidebar row and the body it addresses by the same id. Together
// they are what keeps updates inside the window instead of in a second one.
const panelRow = registrations.find((entry) => entry.name === 'sidebar.panellist')
check(Boolean(panelRow), 'apply() contributes a sidebar panel row')
check(panelRow?.id === 'desktop-updates', 'the panel row carries the id the layout pairs with')
check(typeof panelRow?.label === 'function' && panelRow.label() === 'Updates', 'the panel row is labelled Updates')

const panel = registrations.find((entry) => entry.name === 'main')
check(Boolean(panel), 'apply() registers the panel body in the layout main slot')
check(panel?.key === 'desktop-updates', 'the panel body answers the same id as its row')

const marker = window.__dshDesktopUpdates
check(Boolean(marker), 'the bundle leaves a marker the app can read')
check(
  Array.isArray(marker?.registered) && marker.registered.includes('sidebar.footer.action')
  && marker.registered.includes('settings.section') && marker.registered.includes('sidebar.panellist')
  && marker.registered.includes('main'),
  'the marker names every registered slot',
)
check(marker?.panel === true, 'the marker reports the panel as registered')
check(typeof marker?.openPanel === 'function', 'the marker exposes the panel opener for the app')
check(Array.isArray(marker?.failed) && marker.failed.length === 0, 'the marker reports no failed contribution')
check(marker?.plugin === 'dsh-plugin-desktop-updates', 'the marker names the plugin')

// the sidebar entry, wide and rail. Each render is a fresh mount, so the state
// set below is what the component sees.
bridgeState = { ...bridgeState, app: { ...bridgeState.app, available: false, latest: '2026.09.16' } }
const quietEntry = render(footer.component, propsFor(footer, { wide: true }))
check(textOf(quietEntry).includes('Check for updates'), 'the wide sidebar entry is labelled')

bridgeState = { ...bridgeState, app: { ...bridgeState.app, available: true, latest: '2026.10.01' } }
const alertEntry = render(footer.component, propsFor(footer, { wide: true }))
check(textOf(alertEntry).includes('Update available'), 'the entry announces an available update')
const railEntry = render(footer.component, propsFor(footer, { wide: false }))
check(textOf(railEntry).includes('\u21bb'), 'the rail form shows the icon')

// Clicking the sidebar entry opens updates by selecting the panel in this
// window - no bridge call, no second window.
selectedPanels.length = 0
posted.length = 0
const entryButton = findAction(alertEntry, 'Update available')
check(Boolean(entryButton), 'the sidebar entry is clickable')
if (entryButton) {
  entryButton.props.onClick()
  check(selectedPanels.includes('desktop-updates'), 'clicking it selects the updates panel in this window')
  check(openRequests().length === 0, 'opening the panel asks the app for nothing')
}

// A shell that does not declare the panel slot cannot be asked for one: the
// plugin falls back to the app's native window instead of doing nothing.
layoutAvailable = false
posted.length = 0
try {
  window.__dshDesktopUpdates.openPanel()
} catch (error) {
  check(false, 'the fallback opener does not throw: ' + error.message)
}
layoutAvailable = true
check(openRequests().length === 1, 'without a layout service it falls back to the app window')
posted.length = 0

// the settings section, with the bridge
const sectionTree = render(section.component, propsFor(section, { close: () => {} }))
const sectionText = textOf(sectionTree)
check(sectionText.includes('2026.09.16'), 'the section shows the installed app version')
check(sectionText.includes('2026.10.01'), 'the section shows the newest published version')
check(sectionText.includes('Download and install'), 'the section offers the install action')
check(sectionText.includes('Restart to apply'), 'the section offers the restart action')
check(sectionText.includes('0.1.5-rc.1'), 'the section shows the harness versions')
check(sectionText.includes('dsh-find-plugin'), 'the section lists this home\u2019s plugins')
check(sectionText.includes('base profile'), 'the base bundles are marked as such')

// The section's own way out of settings: close the modal, then select the
// panel, so the switch is visible rather than hidden behind the modal.
let closedSettings = false
const fullView = findAction(render(section.component, propsFor(section, { close: () => { closedSettings = true } })), 'Open the full view')
check(Boolean(fullView), 'the section offers the full view')
if (fullView) {
  selectedPanels.length = 0
  fullView.props.onClick()
  check(closedSettings, 'opening the full view leaves settings first')
  check(selectedPanels.includes('desktop-updates'), 'and selects the updates panel')
}

// The panel itself: the same facts, rendered where the sidebar row points.
const panelTree = render(panel.component, propsFor(panel, {}))
const panelText = textOf(panelTree)
check(panelText.includes('Updates'), 'the panel is titled Updates')
check(panelText.includes('2026.09.16'), 'the panel shows the installed app version')
check(panelText.includes('dsh-find-plugin'), 'the panel lists this home\u2019s plugins')
check(
  !panelText.includes('Open the full view'),
  'the panel does not offer to open itself',
)

// A contribution the shell rejected renders nowhere, so the section has to say
// so: this is the only surface a user ever sees for it.
check(
  !sectionText.includes('Part of this page did not load'),
  'a clean boot shows no rejected-contribution warning',
)
window.__dshDesktopUpdates.failed.push('sidebar.footer.action: list slot requires options.id')
const warnedText = textOf(render(section.component, propsFor(section, { close: () => {} })))
check(
  warnedText.includes('Part of this page did not load')
  && warnedText.includes('list slot requires options.id'),
  'a rejected contribution is reported in the section',
)
window.__dshDesktopUpdates.failed.length = 0

// the bridge contract is actually used
check(stateListener !== null, 'the section subscribes to app state')

// without the bridge the section must say so rather than pretend
delete window.__dshDesktop
const orphanTree = render(section.component, propsFor(section, { close: () => {} }))
const orphanText = textOf(orphanTree)
check(
  orphanText.includes('not running inside the DeepSeek Harness desktop app'),
  'without the bridge the section says the app is not connected',
)
window.__dshDesktop = bridge

// the plugin manager's own controls
const listTree = render(section.component, propsFor(section, { close: () => {} }))
const disableButton = findAction(listTree, 'Enable')
check(Boolean(disableButton), 'a disabled plugin offers Enable')
const removeButton = findAction(listTree, 'Remove')
check(Boolean(removeButton), 'a removable plugin offers Remove')
const installField = findAction(listTree, 'Install plugin')
check(Boolean(installField), 'the manager offers an install action')

console.log('')
console.log(passed + ' passed, ' + failed + ' failed')
process.exit(failed > 0 ? 1 : 0)
