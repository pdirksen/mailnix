import { describe, expect, it } from 'vitest'
import { formatRelative } from './format'

describe('formatRelative', () => {
  const now = new Date('2026-10-24T12:00:00Z')

  it.each([
    ['2026-10-24T11:55:00Z', 'en', '5 minutes ago'],
    ['2026-10-24T09:00:00Z', 'en', '3 hours ago'],
    ['2026-10-23T12:00:00Z', 'en', 'yesterday'],
    ['2026-10-24T11:55:00Z', 'de', 'vor 5 Minuten'],
    ['2026-10-24T11:59:58Z', 'en', '2 seconds ago'],
  ])('%s in %s → %s', (date, locale, expected) => {
    expect(formatRelative(date, locale, now)).toBe(expected)
  })
})
