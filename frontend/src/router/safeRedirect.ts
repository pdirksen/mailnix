/**
 * Returns `target` if it is a same-origin path, otherwise `fallback`.
 * Prevents open redirects via the `?redirect=` query parameter (e.g. `//evil.example` or `https://…`).
 */
export function safeRedirect(target: unknown, fallback = '/'): string {
  if (typeof target !== 'string' || !target.startsWith('/') || target.startsWith('//') || target.startsWith('/\\')) {
    return fallback
  }
  return target
}
