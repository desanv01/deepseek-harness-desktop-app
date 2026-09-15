/**
 * Host half of dsh-plugin-desktop-updates.
 *
 * Presentation only: every fact this plugin shows comes from the desktop app
 * through the page bridge, so the node side provides no service. It still has to
 * be a valid Cordis function plugin, because a bundle row mounts it.
 */
export const name = 'dsh-plugin-desktop-updates'

/** No services are required on the host side. */
export const inject = []

export function apply() {
  // presentation-only: the browser half owns the whole feature
}
