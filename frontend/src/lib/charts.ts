import type { Schemas } from '@/api/client'
import { useMutationObserver } from '@vueuse/core'
import { BarChart, LineChart } from 'echarts/charts'
import { GridComponent, LegendComponent, TooltipComponent } from 'echarts/components'
import { use } from 'echarts/core'
import { CanvasRenderer } from 'echarts/renderers'
import { ref } from 'vue'

// Only what the stats use, so the bundle stays small.
use([BarChart, LineChart, GridComponent, LegendComponent, TooltipComponent, CanvasRenderer])

export type ActionType = Schemas['ActionType']

/**
 * Categorical palette (validated for color-vision deficiencies against the app's card surfaces, light #ffffff and
 * dark #171717). Slots are assigned to things once and never re-ordered, so a color always means the same thing.
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

export function isDark() {
  return typeof document !== 'undefined' && document.documentElement.classList.contains('dark')
}

/** Follows the theme toggle (the `dark` class on <html>). */
export function useDarkMode() {
  const dark = ref(isDark())
  if (typeof document !== 'undefined') {
    useMutationObserver(document.documentElement, () => (dark.value = isDark()), {
      attributes: true,
      attributeFilter: ['class'],
    })
  }
  return dark
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
export function chrome(dark: boolean) {
  return dark
    ? {
        text: '#fafafa',
        muted: '#a1a1a1',
        grid: '#2c2c2a',
        axis: '#383835',
        surface: '#171717',
        tooltip: '#262626',
        border: '#383835',
      }
    : {
        text: '#0a0a0a',
        muted: '#737373',
        grid: '#ebebeb',
        axis: '#c3c2b7',
        surface: '#ffffff',
        tooltip: '#ffffff',
        border: '#e5e5e5',
      }
}

/** Compact axis numbers: 950, 1.2K, 34K. */
export function compact(value: number, locale: string) {
  return new Intl.NumberFormat(locale, { notation: 'compact', maximumFractionDigits: 1 }).format(value)
}
