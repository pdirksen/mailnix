import { api, type Schemas, unwrap } from '@/api/client'
import type { Granularity } from '@/components/stats/buckets'
import type { SeriesStyle, TimeSeries } from '@/components/stats/TimeSeriesChart.vue'
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { ACTION_GROUP, ACTION_GROUPS, type ActionGroup, type ActionType, groupColor } from './charts'

export type Overview = Schemas['OverviewResponse']
export type RuleStats = Schemas['RuleStatsRow']
export type ActionEntry = Schemas['ActionEntry']

export interface StatsParams {
  from?: string
  to?: string
  accountId?: string
  granularity?: Granularity
}

export const statsApi = {
  overview: async (accountId?: string) =>
    unwrap(await api.GET('/api/stats/overview', { params: { query: { accountId } } })),
  timeSeries: async (query: StatsParams & { metric?: string; groupBy?: string; ruleId?: string }) =>
    unwrap(await api.GET('/api/stats/timeseries', { params: { query } })),
  rules: async (query: StatsParams) => unwrap(await api.GET('/api/stats/rules', { params: { query } })),
  senders: async (query: StatsParams) => unwrap(await api.GET('/api/stats/senders', { params: { query } })),
  folders: async (query: StatsParams) => unwrap(await api.GET('/api/stats/folders', { params: { query } })),
  security: async (query: StatsParams) => unwrap(await api.GET('/api/stats/security', { params: { query } })),
  reliability: async (query: StatsParams) => unwrap(await api.GET('/api/stats/reliability', { params: { query } })),
  actions: async (query: {
    accountId?: string
    ruleId?: string
    action?: ActionType
    from?: string
    to?: string
    failedOnly?: boolean
    before?: string
    limit?: number
  }) => unwrap(await api.GET('/api/activity/actions', { params: { query } })),
  undoActions: async (actionIds: string[]) =>
    unwrap(await api.POST('/api/activity/actions/undo', { body: { actionIds } })),
}

/** URL of a section's CSV download with the current filters. */
export function exportUrl(section: string, params: StatsParams) {
  const query = new URLSearchParams({ section })
  for (const [key, value] of Object.entries(params)) if (value) query.set(key, value)
  return `/api/stats/export?${query}`
}

/** Merges per-action series (MoveTo, Delete, ...) into the six action groups, in their fixed order. */
export function groupByAction(data: TimeSeries): TimeSeries {
  const groups = new Map<ActionGroup, number[]>()
  for (const series of data.series) {
    const group = ACTION_GROUP[series.key as ActionType]
    if (!group) continue
    const values = groups.get(group) ?? new Array<number>(data.buckets.length).fill(0)
    series.values.forEach((v, i) => (values[i]! += v))
    groups.set(group, values)
  }
  return {
    buckets: data.buckets,
    series: ACTION_GROUPS.filter((g) => groups.has(g)).map((g) => ({ key: g, name: null, values: groups.get(g)! })),
  }
}

/** Labels and colors for action-group series. */
export function useActionGroupStyle(dark: { value: boolean }) {
  const { t } = useI18n()
  return (key: string): SeriesStyle => ({
    label: t(`stats.groups.${key}`),
    color: groupColor(key as ActionGroup, dark.value),
  })
}

const PRESETS = { '7d': 7, '30d': 30, '90d': 90, '12m': 365 } as const
export type RangePreset = keyof typeof PRESETS
export const RANGE_PRESETS = Object.keys(PRESETS) as RangePreset[]

function isoDay(date: Date) {
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`
}

/** Stats filters, kept in the URL so a view can be bookmarked or shared. */
export function useStatsFilters() {
  const route = useRoute()
  const router = useRouter()
  const text = (name: string) => (typeof route.query[name] === 'string' ? (route.query[name] as string) : undefined)

  const range = computed<RangePreset>({
    get: () =>
      (text('range') as RangePreset | undefined) && text('range')! in PRESETS ? (text('range') as RangePreset) : '30d',
    set: (value) => void router.replace({ query: { ...route.query, range: value, granularity: undefined } }),
  })
  const accountId = computed<string | undefined>({
    get: () => text('account'),
    set: (value) => void router.replace({ query: { ...route.query, account: value || undefined } }),
  })
  const granularity = computed<Granularity>({
    get: () => {
      const chosen = text('granularity')
      if (chosen === 'Day' || chosen === 'Week' || chosen === 'Month') return chosen
      return range.value === '12m' ? 'Month' : range.value === '90d' ? 'Week' : 'Day'
    },
    set: (value) => void router.replace({ query: { ...route.query, granularity: value } }),
  })

  const params = computed<StatsParams>(() => {
    const to = new Date()
    const from = new Date()
    from.setDate(from.getDate() - (PRESETS[range.value] - 1))
    return { from: isoDay(from), to: isoDay(to), accountId: accountId.value, granularity: granularity.value }
  })

  return { range, accountId, granularity, params }
}
