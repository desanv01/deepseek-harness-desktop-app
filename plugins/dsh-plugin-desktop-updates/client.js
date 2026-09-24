/**
 * Browser half of dsh-plugin-desktop-updates.
 *
 * A hand-written module-face bundle: the shell's module loader registers it with
 * `window.__ModuleLoader__.load({ id, factory })`, and the factory receives the
 * frozen module table's `require`. React and the client libraries are baseline
 * modules, so nothing has to be declared to reach them.
 *
 * The plugin contributes two things:
 *
 *   sidebar.footer.action  a row beside Settings that opens the updates panel
 *   settings.section       the Updates section itself
 *
 * Everything it shows comes from the desktop app through `window.__dshDesktop`,
 * the bridge the app injects. In a plain browser tab that bridge is absent and
 * the plugin says so instead of pretending.
 */
window.__ModuleLoader__.load({
	id: 'dsh-plugin-desktop-updates',
	factory: (require) => {
		const React = require('react')
		const h = React.createElement
		const { useCallback, useEffect, useState } = React

		const name = 'dsh-plugin-desktop-updates'
		/**
		 * Identity shared by the sidebar panel row and the panel body: the shell
		 * pairs them by this id, and selecting it is how the plugin opens its own
		 * view in the window.
		 */
		const PANEL_ID = 'desktop-updates'
		/**
		 * Required services: the slot registry, and the layout controller whose
		 * `selectPanel` opens the panel this plugin registers.
		 */
		const inject = ['slots', 'layout']

		/** The native bridge, when this page runs inside the desktop app. */
		function bridge() {
			try {
				return window.__dshDesktop && window.__dshDesktop.version ? window.__dshDesktop : null
			} catch (error) {
				return null
			}
		}

		/**
		 * Says what this plugin did, into the app's log. Without it, a plugin
		 * whose UI does not appear is indistinguishable from one that never
		 * loaded: the harness host logs nothing about browser halves.
		 */
		function report(message) {
			try {
				window.chrome.webview.postMessage(
					JSON.stringify({
						dsh: 1,
						id: 0,
						method: 'pageLog',
						params: { level: 'info', message: message, source: 'dsh-plugin-desktop-updates', line: 0 },
					}),
				)
			} catch (error) {
				// no host to tell
			}
		}

		/**
		 * The contributions the shell rejected, as the marker recorded them. A
		 * rejected entry renders nowhere and the shell reports nothing, so this
		 * section is the one place the user can see that part of the page is
		 * missing. It is read at render time rather than subscribed to: a
		 * contribution can only fail while the page boots, and the section
		 * re-renders on every state push after that.
		 */
		function rejectedContributions() {
			try {
				const marker = window.__dshDesktopUpdates
				return marker && Array.isArray(marker.failed) ? marker.failed : []
			} catch (error) {
				return []
			}
		}

		/** Subscribes to the app's state pushes and returns the live snapshot. */
		function useBridgeState() {
			const [state, setState] = useState(() => {
				const api = bridge()
				return api ? api.state() : null
			})

			useEffect(() => {
				const api = bridge()
				if (!api) return undefined
				return api.subscribe(setState)
			}, [])

			return state
		}

		/** Runs a bridge call and reports failures without throwing into render. */
		function useAction() {
			const [busy, setBusy] = useState('')
			const [error, setError] = useState('')

			const run = useCallback((method, label, params) => {
				const api = bridge()
				if (!api) {
					setError('The desktop app is not connected.')
					return Promise.resolve(null)
				}
				setBusy(label || method)
				setError('')
				return Promise.resolve(api[method](params)).then(
					(result) => {
						setBusy('')
						if (result && result.error) setError(result.error)
						return result
					},
					(failure) => {
						setBusy('')
						setError(String((failure && failure.message) || failure))
						return null
					},
				)
			}, [])

			return { busy, error, run, setError }
		}

		// ---- presentation ------------------------------------------------------

		const surface = {
			background: 'rgba(127,127,127,0.08)',
			border: '1px solid rgba(127,127,127,0.28)',
			borderRadius: '10px',
		}

		function Row(props) {
			return h(
				'div',
				{ style: { display: 'flex', gap: '10px', padding: '5px 0', alignItems: 'baseline' } },
				h('span', { style: { minWidth: '150px', opacity: 0.65, fontSize: '12.5px' } }, props.label),
				h('span', { style: { fontSize: '13.5px', fontWeight: props.strong ? 600 : 400 } }, props.value),
			)
		}

		function Button(props) {
			const disabled = props.disabled || props.busy
			return h(
				'button',
				{
					type: 'button',
					onClick: props.onClick,
					disabled: disabled,
					title: props.title || '',
					style: {
						padding: '7px 13px',
						borderRadius: '8px',
						border: props.primary ? 'none' : '1px solid rgba(127,127,127,0.35)',
						background: props.primary ? '#2f6df6' : 'transparent',
						color: props.primary ? '#fff' : 'inherit',
						font: 'inherit',
						fontSize: '13px',
						cursor: disabled ? 'default' : 'pointer',
						opacity: disabled ? 0.5 : 1,
					},
				},
				props.busy ? props.busy : props.children,
			)
		}

		/**
		 * Release notes arrive as markdown. Rather than render markup, strip the
		 * syntax that would otherwise show up literally and keep the structure:
		 * headings, bullets and blank lines.
		 */
		function plainNotes(text) {
			return String(text || '')
				.replace(/\\r\\n|\\n/g, '\n')
				.replace(/^#{1,6}\s*/gm, '')
				.replace(/\*\*(.+?)\*\*/g, '$1')
				.replace(/`([^`]+)`/g, '$1')
				.replace(/\[([^\]]+)\]\(([^)]+)\)/g, '$1 ($2)')
				.replace(/^[-*+]\s+/gm, '\u2022  ')
				.replace(/\n{3,}/g, '\n\n')
				.trim()
		}

		function Notes(props) {
			const text = plainNotes(props.text)
			if (!text) return null
			return h(
				'div',
				{
					style: Object.assign({}, surface, {
						marginTop: '10px',
						padding: '10px 12px',
						maxHeight: '190px',
						overflow: 'auto',
						whiteSpace: 'pre-wrap',
						fontSize: '12.5px',
						lineHeight: 1.5,
						opacity: 0.9,
					}),
				},
				text,
			)
		}

		function Status(props) {
			if (!props.text) return null
			return h(
				'div',
				{
					style: {
						marginTop: '8px',
						fontSize: '12.5px',
						color: props.bad ? '#e5534b' : 'inherit',
						opacity: props.bad ? 1 : 0.75,
					},
				},
				props.text,
			)
		}

		function Progress(props) {
			if (!props.value) return null
			const width = props.total > 0 ? Math.min(100, Math.round((props.received * 100) / props.total)) : 8
			return h(
				'div',
				{ style: { marginTop: '10px' } },
				h(
					'div',
					{ style: { height: '6px', borderRadius: '3px', background: 'rgba(127,127,127,0.25)' } },
					h('div', { style: { height: '6px', borderRadius: '3px', width: width + '%', background: '#2f6df6' } }),
				),
				h('div', { style: { marginTop: '5px', fontSize: '12px', opacity: 0.7 } }, props.label || ''),
			)
		}

		/** The sidebar footer entry: a labelled row beside Settings, or an icon. */
		function UpdatesFooterAction(props) {
			const wide = Boolean(props && props.wide)
			const state = useBridgeState()
			const available = Boolean(state && state.app && state.app.available)
			const open = (props && props.openUpdates) || function () {}

			return h(
				'button',
				{
					type: 'button',
					title: available ? 'An update is available' : 'Check for updates',
					'aria-label': 'Check for updates',
					onClick: () => open(),
					style: {
						display: 'flex',
						alignItems: 'center',
						justifyContent: wide ? 'flex-start' : 'center',
						gap: '9px',
						width: '100%',
						padding: wide ? '8px 10px' : '8px 0',
						border: 'none',
						borderRadius: '8px',
						background: 'transparent',
						color: 'inherit',
						font: 'inherit',
						fontSize: '13px',
						cursor: 'pointer',
						opacity: 0.9,
					},
				},
				h(
					'span',
					{ 'aria-hidden': 'true', style: { position: 'relative', display: 'inline-flex', fontSize: '14px' } },
					'\u21bb',
					available
						? h('span', {
								style: {
									position: 'absolute',
									top: '-1px',
									right: '-3px',
									width: '7px',
									height: '7px',
									borderRadius: '50%',
									background: '#2f6df6',
								},
							})
						: null,
				),
				wide ? h('span', null, available ? 'Update available' : 'Check for updates') : null,
			)
		}

		/**
		 * The updates body: the same facts and actions in both hosts, the
		 * Settings section and the central panel. Neither host owns the data -
		 * it arrives from the app through the bridge - so the two can never
		 * disagree about what is installed or what is available.
		 */
		function UpdatesBody(props) {
			const state = useBridgeState()
			const action = useAction()
			const [progress, setProgress] = useState(null)
			const inPanel = Boolean(props && props.panel)
			const close = (props && props.close) || null
			const openUpdates = (props && props.openUpdates) || function () {}

			useEffect(() => {
				const api = bridge()
				if (!api || !api.onProgress) return undefined
				return api.onProgress(setProgress)
			}, [])

			if (!bridge()) {
				return h(
					'div',
					{ style: { padding: '4px 2px', fontSize: '13px', lineHeight: 1.6 } },
					h('div', { style: { fontWeight: 600, marginBottom: '6px' } }, 'Updates'),
					h(
						'div',
						{ style: { opacity: 0.75 } },
						'This page is not running inside the DeepSeek Harness desktop app, so there is nothing it can update. ',
						'Open the app to check for a new build, or install the harness with npm: ',
						h('code', null, 'npm install -g @deepseek-ai/dsh@latest'),
					),
				)
			}

			const app = (state && state.app) || {}
			const harness = (state && state.harness) || {}
			const staged = state && state.staged
			const notes = app.notes
			const rejected = rejectedContributions()

			return h(
				'div',
				{ style: { padding: inPanel ? '0' : '2px', fontSize: '13px' } },
				h('div', { style: { fontWeight: 600, fontSize: '14px', marginBottom: '2px' } }, 'Updates'),
				h(
					'div',
					{ style: { opacity: 0.7, fontSize: '12.5px', marginBottom: '12px' } },
					'Desktop app, harness, and the plugins this home runs.',
				),

				rejected.length > 0
					? h(Status, {
							text: 'Part of this page did not load: ' + rejected.join('; ')
								+ '. Everything else here still works.',
							bad: true,
						})
					: null,

				h(Row, { label: 'Desktop app', value: app.version || 'unknown', strong: true }),
				h(Row, { label: 'Newest published', value: app.latest || 'not checked' }),
				h(Status, {
					text: action.error || app.status || 'No check has run yet.',
					bad: Boolean(action.error),
				}),
				h(Progress, {
					value: progress && progress.received,
					received: progress ? progress.received : 0,
					total: progress ? progress.total : 0,
					label: progress ? progress.phase + (progress.total ? ' — ' + progress.percent + '%' : '') : '',
				}),

				h(Notes, { text: notes }),

				h(
					'div',
					{ style: { display: 'flex', gap: '8px', flexWrap: 'wrap', marginTop: '12px' } },
					h(Button, {
						onClick: () => action.run('check', 'Checking ...'),
						busy: action.busy === 'Checking ...' ? 'Checking ...' : null,
						children: 'Check now',
					}),
					h(Button, {
						primary: true,
						disabled: !app.available || Boolean(staged),
						onClick: () => action.run('install', 'Downloading ...'),
						busy: action.busy === 'Downloading ...' ? 'Downloading ...' : null,
						children: 'Download and install',
					}),
					h(Button, {
						disabled: !staged,
						onClick: () => action.run('apply', 'Restarting ...'),
						children: 'Restart to apply',
					}),
					inPanel
						? null
						: h(Button, {
								onClick: () => {
									// The panel renders behind the settings panel, so
									// leaving settings first is what makes the switch
									// visible. `close` is absent outside the shell's
									// settings host, where the button then just opens.
									if (close) close()
									openUpdates()
								},
								children: 'Open the full view',
							}),
				),

				h(
					'label',
					{ style: { display: 'flex', gap: '8px', alignItems: 'center', marginTop: '14px', fontSize: '12.5px' } },
					h('input', {
						type: 'checkbox',
						checked: Boolean(state && state.policy && state.policy.checkOnLaunch),
						onChange: (event) => action.run('setPolicy', '', { checkOnLaunch: event.target.checked }),
					}),
					'Check for updates when the app starts',
				),

				h('div', {
					style: { height: '1px', background: 'rgba(127,127,127,0.25)', margin: '18px 0 14px' },
				}),

				h('div', { style: { fontWeight: 600, marginBottom: '2px' } }, 'Harness'),
				h(Row, { label: 'Installed', value: harness.installed || 'not installed' }),
				h(Row, { label: 'npm latest', value: harness.latest || 'unknown' }),
				h(Row, { label: 'npm alpha', value: harness.alpha || 'unknown' }),
				h(Status, { text: harness.status || '' }),
				h(
					'div',
					{ style: { opacity: 0.65, fontSize: '11.5px', marginTop: '6px' } },
					'A new harness is verified by the next start. If a plugin cannot load on it, the app disables '
						+ 'that plugin, says which one, and starts anyway.',
				),
				h(
					'div',
					{ style: { display: 'flex', gap: '8px', marginTop: '10px', flexWrap: 'wrap' } },
					h(Button, {
						onClick: () => action.run('harnessCheck', 'Reading npm ...'),
						busy: action.busy === 'Reading npm ...' ? 'Reading npm ...' : null,
						children: 'Check npm',
					}),
					h(Button, {
						disabled: !harness.available,
						onClick: () => action.run('harnessInstall', 'Installing harness ...'),
						busy: action.busy === 'Installing harness ...' ? 'Installing harness ...' : null,
						children: 'Install harness update',
					}),
				),

				h('div', {
					style: { height: '1px', background: 'rgba(127,127,127,0.25)', margin: '18px 0 14px' },
				}),

				h('div', { style: { fontWeight: 600, marginBottom: '6px' } }, 'Plugins in this home'),
				h(PluginList, null),

				close
					? h(
							'div',
							{ style: { marginTop: '18px' } },
							h(Button, { onClick: close, children: 'Close settings' }),
						)
					: null,
			)
		}

		/**
		 * The Updates panel: the full view, in the app's own window, addressed
		 * by the sidebar panel row with the same id. Selecting it is a layout
		 * action the page performs itself, so opening updates never leaves the
		 * window and never depends on the bridge.
		 */
		function UpdatesPanel(props) {
			return h(
				'div',
				{
					style: {
						boxSizing: 'border-box',
						height: '100%',
						overflow: 'auto',
						padding: '22px 26px 30px',
					},
				},
				h('div', { style: { fontSize: '16px', fontWeight: 600, marginBottom: '2px' } }, 'Updates'),
				h(
					'div',
					{ style: { opacity: 0.7, fontSize: '12.5px', marginBottom: '16px' } },
					'DeepSeek Harness desktop app, the harness itself, and the plugins this home runs.',
				),
				h(UpdatesBody, { panel: true, openUpdates: props && props.openUpdates }),
			)
		}

		/** The sidebar panel row's icon; the shell passes its size and state. */
		function UpdatesPanelIcon(props) {
			const size = (props && props.size) || 16
			return h(
				'svg',
				{
					width: size,
					height: size,
					viewBox: '0 0 16 16',
					fill: 'none',
					stroke: 'currentColor',
					strokeWidth: 1.4,
					strokeLinecap: 'round',
					strokeLinejoin: 'round',
					'aria-hidden': 'true',
					style: { opacity: props && props.active ? 1 : 0.8 },
				},
				h('path', { d: 'M13.5 8a5.5 5.5 0 1 1-1.9-4.15' }),
				h('path', { d: 'M13.5 2.5v3.2h-3.2' }),
				h('path', { d: 'M8 5.2V8l2 1.3' }),
			)
		}


		/** The plugin manager: what this home runs, and the switches for it. */
		function PluginList() {
			const state = useBridgeState()
			const action = useAction()
			const [spec, setSpec] = useState('')
			const plugins = (state && state.plugins) || null

			if (!plugins) {
				return h('div', { style: { opacity: 0.7, fontSize: '12.5px' } }, 'Reading the profile ...')
			}

			const canManage = plugins.canManage !== false

			return h(
				'div',
				null,
				plugins.entries.length === 0
					? h('div', { style: { opacity: 0.7, fontSize: '12.5px' } }, 'This home runs the base profile only.')
					: plugins.entries.map((entry) =>
							h(
								'div',
								{
									key: entry.name,
									style: Object.assign({}, surface, {
										display: 'flex',
										alignItems: 'center',
										gap: '10px',
										padding: '8px 10px',
										marginBottom: '6px',
									}),
								},
								h(
									'div',
									{ style: { flex: 1, minWidth: 0 } },
									h('div', { style: { fontSize: '13px', fontWeight: 500 } }, entry.name),
									h(
										'div',
										{ style: { fontSize: '11.5px', opacity: 0.65 } },
										entry.version ? 'v' + entry.version : 'version unknown',
										entry.builtIn ? ' — base profile' : '',
										entry.enabled ? '' : ' — disabled',
									),
								),
								entry.builtIn || !canManage
									? null
									: h(Button, {
											children: entry.enabled ? 'Disable' : 'Enable',
											onClick: () =>
												action.run('setPluginEnabled', '', {
													name: entry.name,
													enabled: !entry.enabled,
												}),
										}),
								entry.builtIn || !canManage
									? null
									: h(Button, {
											children: 'Remove',
											onClick: () => action.run('removePlugin', 'Removing ' + entry.name + ' ...', { name: entry.name }),
											busy: action.busy === 'Removing ' + entry.name + ' ...' ? 'Removing ...' : null,
										}),
							),
						),

				canManage
					? h(
							'div',
							{ style: { display: 'flex', gap: '8px', marginTop: '10px', alignItems: 'center' } },
							h('input', {
								type: 'text',
								value: spec,
								placeholder: 'npm name, git spec, or a local folder',
								onChange: (event) => setSpec(event.target.value),
								style: Object.assign({}, surface, {
									flex: 1,
									minWidth: 0,
									padding: '7px 10px',
									background: 'rgba(127,127,127,0.06)',
									color: 'inherit',
									font: 'inherit',
									fontSize: '12.5px',
								}),
							}),
							h(Button, {
								primary: true,
								disabled: spec.trim().length === 0,
								busy: action.busy === 'Installing ...' ? 'Installing ...' : null,
								onClick: () => {
									const value = spec.trim()
									if (!value) return
									action.run('installPlugin', 'Installing ...', { spec: value }).then((result) => {
										if (result && result.ok) setSpec('')
									})
								},
								children: 'Install plugin',
							}),
						)
					: null,

				action.busy
					? h('div', { style: { marginTop: '8px', fontSize: '12px', opacity: 0.7 } }, action.busy)
					: null,
				action.error ? h(Status, { text: action.error, bad: true }) : null,
			)
		}

		function apply(ctx) {
			// A marker for support and for the app: what this half registered, and
			// whether it found the bridge. The app reads it after the page loads.
			const marker = { plugin: name, bridge: Boolean(bridge()), registered: [], failed: [], panel: false }
			window.__dshDesktopUpdates = marker

			/*
			 * Opening updates is a layout action, not a bridge call: the panel
			 * lives in this page's own window, so the page selects it itself and
			 * the app is not involved at all. That also means opening it cannot
			 * fail because the bridge is unavailable - the one case where it
			 * falls back to the app's native window is a shell without the panel
			 * slot, which is also a shell whose layout service would reject the
			 * id it never registered.
			 */
			const openUpdates = () => {
				try {
					ctx.layout.selectPanel(PANEL_ID)
					return true
				} catch (error) {
					report('opening the updates panel failed: ' + ((error && error.message) || error))
					const api = bridge()
					if (api) api.openNative()
					return false
				}
			}
			marker.openPanel = openUpdates

			// Both slots are list slots: `id` is required, and registering a list
			// entry without one throws rather than rendering nothing. Each
			// contribution is installed on its own, so a slot this build of the
			// shell does not declare cannot take the other one down with it, and
			// a rejection is recorded in the marker and sent to the app's log:
			// the shell retires a failed injection without a word, so that report
			// is the only place a rejected entry becomes visible.
			const contribute = (slot, options, component) => {
				let noted = false
				const note = (error) => {
					noted = true
					const reason = (error && error.message) || String(error)
					marker.failed.push(slot + ': ' + reason)
					report('registering into ' + slot + ' failed: ' + reason)
					return error
				}

				try {
					ctx.slots.inject(slot, () => {
						try {
							const dispose = ctx.slots.register(options, component)
							marker.registered.push(slot)
							if (slot === 'main') marker.panel = true
							return dispose
						} catch (error) {
							// A callback that runs after apply() fails inside
							// the registry's declaration listener, which retires
							// the injection and rethrows - so this one keeps its
							// throw.
							throw note(error)
						}
					})
				} catch (error) {
					// A slot already declared when apply() runs fails here
					// instead, as does a wait that could not even be installed.
					// The failure stays contained: the other half of the plugin
					// is still worth having, and the note above is what the app
					// reads.
					if (!noted) note(error)
				}
			}

			contribute(
				'sidebar.footer.action',
				{
					name: 'sidebar.footer.action',
					id: 'desktop-updates',
					order: 20,
					label: () => 'Check for updates',
					inject: () => ({ openUpdates }),
				},
				UpdatesFooterAction,
			)
			contribute(
				'sidebar.panellist',
				{
					name: 'sidebar.panellist',
					id: PANEL_ID,
					order: 30,
					label: () => 'Updates',
				},
				UpdatesPanelIcon,
			)
			contribute(
				'main',
				{ name: 'main', key: PANEL_ID, inject: () => ({ openUpdates }) },
				UpdatesPanel,
			)
			contribute(
				'settings.section',
				{
					name: 'settings.section',
					id: 'desktop-updates',
					order: 15,
					label: () => 'Updates',
					inject: () => ({ openUpdates }),
				},
				UpdatesBody,
			)

			report('applied: ' + JSON.stringify(marker))
		}

		return { name, inject, apply }
	},
})
