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
		/** Required service: the slot registry. */
		const inject = ['slots']

		/** The native bridge, when this page runs inside the desktop app. */
		function bridge() {
			try {
				return window.__dshDesktop && window.__dshDesktop.version ? window.__dshDesktop : null
			} catch (error) {
				return null
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

			return h(
				'button',
				{
					type: 'button',
					title: available ? 'An update is available' : 'Check for updates',
					'aria-label': 'Check for updates',
					onClick: () => {
						const api = bridge()
						if (api) api.openNative()
					},
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

		/** The Updates section inside Settings. */
		function UpdatesSection(props) {
			const state = useBridgeState()
			const action = useAction()
			const [progress, setProgress] = useState(null)

			useEffect(() => {
				const api = bridge()
				if (!api || !api.onProgress) return undefined
				return api.onProgress(setProgress)
			}, [])

			const close = (props && props.close) || function () {}

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

			return h(
				'div',
				{ style: { padding: '2px', fontSize: '13px' } },
				h('div', { style: { fontWeight: 600, fontSize: '14px', marginBottom: '2px' } }, 'Updates'),
				h(
					'div',
					{ style: { opacity: 0.7, fontSize: '12.5px', marginBottom: '12px' } },
					'Desktop app, harness, and the plugins this home runs.',
				),

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
					h(Button, {
						onClick: () => action.run('openNative', ''),
						children: 'Open the app window',
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

		/** The plugin manager: what this home runs, and the switches for it. */
		function PluginList() {
			const state = useBridgeState()
			const action = useAction()
			const plugins = (state && state.plugins) || null

			if (!plugins) {
				return h('div', { style: { opacity: 0.7, fontSize: '12.5px' } }, 'Reading the profile ...')
			}

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
								entry.builtIn
									? null
									: h(Button, {
											children: entry.enabled ? 'Disable' : 'Enable',
											onClick: () =>
												action.run('setPluginEnabled', '', {
													name: entry.name,
													enabled: !entry.enabled,
												}),
										}),
							),
						),
				action.error ? h(Status, { text: action.error, bad: true }) : null,
			)
		}

		function apply(ctx) {
			ctx.slots.inject('sidebar.footer.action', () =>
				ctx.slots.register({ name: 'sidebar.footer.action' }, UpdatesFooterAction),
			)
			ctx.slots.inject('settings.section', () =>
				ctx.slots.register(
					{
						name: 'settings.section',
						id: 'desktop-updates',
						order: 15,
						label: () => 'Updates',
					},
					UpdatesSection,
				),
			)
		}

		return { name, inject, apply }
	},
})
