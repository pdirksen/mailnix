import { describe, expect, it } from 'vitest'
import { safeRedirect } from './safeRedirect'

describe('safeRedirect', () => {
  it.each(['/', '/rules/42', '/stats?from=2026-01-01#volume'])('keeps same-origin path %s', (path) => {
    expect(safeRedirect(path)).toBe(path)
  })

  it.each(['//evil.example', '/\\evil.example', 'https://evil.example', 'javascript:alert(1)', '', undefined, ['/a']])(
    'rejects %s',
    (target) => {
      expect(safeRedirect(target)).toBe('/')
    },
  )

  it('uses the given fallback', () => {
    expect(safeRedirect('//evil.example', '/login')).toBe('/login')
  })
})
