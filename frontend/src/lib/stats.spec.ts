import { bucketEnd, formatBucket } from '@/components/stats/buckets'
import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import { defineComponent, h, nextTick } from 'vue'
import { createMemoryHistory, createRouter } from 'vue-router'
import { exportUrl, groupByAction, useStatsFilters } from './stats'

describe('groupByAction', () => {
  it('merges actions into their groups in a fixed order, whatever the input order', () => {
    const grouped = groupByAction({
      buckets: ['2026-10-23', '2026-10-24'],
      series: [
        { key: 'Flag', name: null, values: [1, 0] },
        { key: 'Archive', name: null, values: [0, 2] },
        { key: 'MoveTo', name: null, values: [3, 1] },
        { key: 'Unflag', name: null, values: [0, 1] },
      ],
    })

    expect(grouped.series).toEqual([
      { key: 'moved', name: null, values: [3, 3] },
      { key: 'flags', name: null, values: [1, 1] },
    ])
  })
})

describe('buckets', () => {
  it('knows where a week or month ends, for drill-down', () => {
    expect(bucketEnd('2026-10-19', 'Week')).toBe('2026-10-25')
    expect(bucketEnd('2026-02-01', 'Month')).toBe('2026-02-28')
    expect(bucketEnd('2026-10-24', 'Day')).toBe('2026-10-24')
    expect(formatBucket('2026-10-01', 'Month', 'en')).toBe('Oct 2026')
  })
})

describe('useStatsFilters', () => {
  async function setup(path: string) {
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/stats', component: { render: () => null } }],
    })
    await router.push(path)
    let filters!: ReturnType<typeof useStatsFilters>
    mount(defineComponent({ setup: () => ((filters = useStatsFilters()), () => h('div')) }), {
      global: { plugins: [router] },
    })
    return { router, filters }
  }

  it('reads and writes the filters in the URL, with a sensible granularity per period', async () => {
    const { router, filters } = await setup('/stats?range=90d&account=a1')

    expect(filters.range.value).toBe('90d')
    expect(filters.granularity.value).toBe('Week')
    expect(filters.params.value.accountId).toBe('a1')

    filters.range.value = '12m'
    await nextTick()
    await router.isReady()
    await new Promise((r) => setTimeout(r))
    expect(router.currentRoute.value.query).toMatchObject({ range: '12m', account: 'a1' })
    expect(filters.granularity.value).toBe('Month')

    const { from, to } = filters.params.value
    expect((new Date(to!).getTime() - new Date(from!).getTime()) / 86_400_000).toBe(364)
  })

  it('falls back to the last 30 days', async () => {
    const { filters } = await setup('/stats?range=nonsense')

    expect(filters.range.value).toBe('30d')
    expect(filters.granularity.value).toBe('Day')
  })
})

describe('exportUrl', () => {
  it('carries the filters', () => {
    expect(exportUrl('rules', { from: '2026-10-01', to: '2026-10-24', accountId: undefined, granularity: 'Day' })).toBe(
      '/api/stats/export?section=rules&from=2026-10-01&to=2026-10-24&granularity=Day',
    )
  })
})
