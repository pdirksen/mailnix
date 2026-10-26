import {
  BookOpenIcon,
  Flower2Icon,
  GrapeIcon,
  LeafIcon,
  MonitorIcon,
  MoonIcon,
  MoonStarIcon,
  SunIcon,
  TreesIcon,
} from '@lucide/vue'
import type { Component } from 'vue'

export const THEMES = ['light', 'dark', 'sepia', 'rose', 'sage', 'midnight', 'forest', 'plum'] as const
export type Theme = (typeof THEMES)[number]
export type ThemePreference = Theme | 'auto'

/**
 * The classes each theme puts on <html>; the theme tokens in main.css key off them. Dark-based themes also carry
 * `dark`, so `dark:` utilities, the chart palette and `color-scheme` follow. The color themes use a `theme-` prefix:
 * a bare `sepia` is a Tailwind utility (a sepia filter over the whole page).
 */
export const THEME_CLASSES: Record<Theme, string> = {
  light: 'light',
  dark: 'dark',
  sepia: 'theme-sepia',
  rose: 'theme-rose',
  sage: 'theme-sage',
  midnight: 'dark theme-midnight',
  forest: 'dark theme-forest',
  plum: 'dark theme-plum',
}

export function isDarkTheme(theme: Theme) {
  return THEME_CLASSES[theme].split(' ').includes('dark')
}

/** The theme the classes on an element stand for. */
export function themeOf(classes: DOMTokenList): Theme {
  return THEMES.find((theme) => classes.contains(`theme-${theme}`)) ?? (classes.contains('dark') ? 'dark' : 'light')
}

export interface ThemeOption {
  value: ThemePreference
  labelKey: string
  icon: Component
}

export const BASE_THEME_OPTIONS: ThemeOption[] = [
  { value: 'light', labelKey: 'common.themeLight', icon: SunIcon },
  { value: 'dark', labelKey: 'common.themeDark', icon: MoonIcon },
  { value: 'auto', labelKey: 'common.themeSystem', icon: MonitorIcon },
]

/** Light ones first, then dark ones. */
export const COLOR_THEME_OPTIONS: ThemeOption[] = [
  { value: 'sepia', labelKey: 'common.themeSepia', icon: BookOpenIcon },
  { value: 'rose', labelKey: 'common.themeRose', icon: Flower2Icon },
  { value: 'sage', labelKey: 'common.themeSage', icon: LeafIcon },
  { value: 'midnight', labelKey: 'common.themeMidnight', icon: MoonStarIcon },
  { value: 'forest', labelKey: 'common.themeForest', icon: TreesIcon },
  { value: 'plum', labelKey: 'common.themePlum', icon: GrapeIcon },
]
