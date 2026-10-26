import { describe, expect, it } from 'vitest'
import { BASE_THEME_OPTIONS, COLOR_THEME_OPTIONS, isDarkTheme, THEME_CLASSES, THEMES, themeOf } from './themes'

function classList(classes: string) {
  const element = document.createElement('div')
  element.className = classes
  return element.classList
}

describe('themes', () => {
  it.each(THEMES)('recognizes %s from its classes', (theme) => {
    expect(themeOf(classList(THEME_CLASSES[theme]))).toBe(theme)
  })

  it('marks the dark-based themes with the dark class', () => {
    expect(THEMES.filter(isDarkTheme)).toEqual(['dark', 'midnight', 'forest', 'plum'])
  })

  it('offers every theme once', () => {
    const offered = [...BASE_THEME_OPTIONS, ...COLOR_THEME_OPTIONS].map((option) => option.value)
    expect(offered.sort()).toEqual([...THEMES, 'auto'].sort())
  })
})
