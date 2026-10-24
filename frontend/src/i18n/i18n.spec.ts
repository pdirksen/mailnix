import { describe, expect, it } from 'vitest'
import { detectLocale } from '.'

describe('detectLocale', () => {
  it('picks the first supported language, ignoring region', () => {
    expect(detectLocale(['fr-FR', 'de-AT', 'en-US'])).toBe('de')
  })

  it('falls back to English', () => {
    expect(detectLocale(['fr-FR', 'es'])).toBe('en')
    expect(detectLocale([])).toBe('en')
  })
})
