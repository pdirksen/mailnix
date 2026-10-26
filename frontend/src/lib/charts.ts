import type { Schemas } from '@/api/client'
import { isDarkTheme, type Theme, themeOf } from '@/lib/themes'
import { useMutationObserver } from '@vueuse/core'
import { BarChart, LineChart } from 'echarts/charts'
import { GridComponent, LegendComponent, TooltipComponent } from 'echarts/components'
import { use } from 'echarts/core'
import { CanvasRenderer } from 'echarts/renderers'
import { computed, ref } from 'vue'

// Only what the stats use, so the bundle stays small.
use([BarChart, LineChart, GridComponent, LegendComponent, TooltipComponent, CanvasRenderer])

export type ActionType = Schemas['ActionType']

/**
 * Categorical palette (validated for color-vision deficiencies against the app's card surfaces, light #ffffff and
 * dark #171717; the color themes use the set of their base). Slots are assigned to things once and never
 * re-ordered, so a color always means the same thing.
 */
const SLOTS = {
  light: ['#2a78d6', '#eb6834', '#1baf7a', '#eda100', '#e87ba4', '#008300'],
  dark: ['#3987e5', '#d95926', '#199e70', '#c98500', '#d55181', '#008300'],
} as const

/** What a rule did to a message, in six groups (more would be too many colors to tell apart). */
export const ACTION_GROUPS = ['moved', 'trashed', 'flags', 'read', 'labels', 'purged'] as const
export type ActionGroup = (typeof ACTION_GROUPS)[number]

export const ACTION_GROUP: Record<ActionType, ActionGroup> = {
  MoveTo: 'moved',
  Archive: 'moved',
  MarkAsSpam: 'moved',
  Delete: 'trashed',
  Flag: 'flags',
  Unflag: 'flags',
  MarkRead: 'read',
  MarkUnread: 'read',
  CopyTo: 'labels',
  AddKeyword: 'labels',
  RemoveKeyword: 'labels',
  Purge: 'purged',
}

/** The action to filter the activity by when a group stands for exactly one. */
export const GROUP_ACTION: Partial<Record<ActionGroup, ActionType>> = { trashed: 'Delete', purged: 'Purge' }

/** The active theme, read from the classes the theme store puts on <html>. */
export function currentTheme(): Theme {
  return typeof document === 'undefined' ? 'light' : themeOf(document.documentElement.classList)
}

/** Follows the theme toggle. */
export function useTheme() {
  const theme = ref(currentTheme())
  if (typeof document !== 'undefined') {
    useMutationObserver(document.documentElement, () => (theme.value = currentTheme()), {
      attributes: true,
      attributeFilter: ['class'],
    })
  }
  return theme
}

/** Whether a dark-based theme is active (the `dark` class on <html>), which picks the categorical palette. */
export function useDarkMode() {
  const theme = useTheme()
  return computed(() => isDarkTheme(theme.value))
}

export function slotColor(slot: number, dark: boolean) {
  const slots = dark ? SLOTS.dark : SLOTS.light
  return slots[slot % slots.length]!
}

export function groupColor(group: ActionGroup, dark: boolean) {
  return slotColor(ACTION_GROUPS.indexOf(group), dark)
}

/**
 * Chart chrome matching the theme tokens (as hex: the canvas renderer does not understand the oklch() tokens).
 * Text never takes a series color; grid and axes stay hairline and quiet.
 */
const CHROME: Record<Theme, Record<'text' | 'muted' | 'grid' | 'axis' | 'surface' | 'tooltip' | 'border', string>> = {
  light: {
    text: '#0a0a0a',
    muted: '#737373',
    grid: '#ebebeb',
    axis: '#c3c2b7',
    surface: '#ffffff',
    tooltip: '#ffffff',
    border: '#e5e5e5',
  },
  dark: {
    text: '#fafafa',
    muted: '#a1a1a1',
    grid: '#2c2c2a',
    axis: '#383835',
    surface: '#171717',
    tooltip: '#262626',
    border: '#383835',
  },
  sepia: {
    text: '#362517',
    muted: '#776554',
    grid: '#ece4d5',
    axis: '#cabba5',
    surface: '#fdfaf2',
    tooltip: '#fdfaf2',
    border: '#e3d8c5',
  },
  midnight: {
    text: '#e9eff6',
    muted: '#98a6b8',
    grid: '#1e2735',
    axis: '#2c3648',
    surface: '#10192a',
    tooltip: '#1b2435',
    border: '#2c3648',
  },
  rose: {
    text: '#341f27',
    muted: '#7a616a',
    grid: '#f0dfe5',
    axis: '#d1b5bf',
    surface: '#fff9fb',
    tooltip: '#fff9fb',
    border: '#ebd6dd',
  },
  sage: {
    text: '#1a2b1d',
    muted: '#5b6f5e',
    grid: '#dce9de',
    axis: '#afc4b2',
    surface: '#f7fdf8',
    tooltip: '#f7fdf8',
    border: '#d1e1d4',
  },
  forest: {
    text: '#e9f1ec',
    muted: '#95ab9e',
    grid: '#1c2a22',
    axis: '#283a30',
    surface: '#0b1d13',
    tooltip: '#17291f',
    border: '#283a30',
  },
  plum: {
    text: '#f0edf4',
    muted: '#aaa0b3',
    grid: '#2a2330',
    axis: '#3a3141',
    surface: '#1d1423',
    tooltip: '#28202f',
    border: '#3a3141',
  },
}

export function chrome(theme: Theme) {
  return CHROME[theme]
}

/** Compact axis numbers: 950, 1.2K, 34K. */
export function compact(value: number, locale: string) {
  return new Intl.NumberFormat(locale, { notation: 'compact', maximumFractionDigits: 1 }).format(value)
}
