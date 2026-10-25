<script setup lang="ts">
import ChartCard from '@/components/stats/ChartCard.vue'
import KpiTile from '@/components/stats/KpiTile.vue'
import SeriesTable from '@/components/stats/SeriesTable.vue'
import TimeSeriesChart, { type TimeSeries } from '@/components/stats/TimeSeriesChart.vue'
import { slotColor, useDarkMode } from '@/lib/charts'
import { useConnectionErrors } from '@/lib/connectionErrors'
import { groupByAction, type RuleStats, statsApi, useActionGroupStyle } from '@/lib/stats'
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'

/** What a saved rule did over the last 30 days. */
const props = defineProps<{ ruleId: string }>()

const { t, locale } = useI18n()
const { describeRequestError } = useConnectionErrors()
const dark = useDarkMode()
const groupStyle = useActionGroupStyle(dark)
const matchedStyle = () => ({ label: t('stats.ruleColumns.matched'), color: slotColor(0, dark.value) })

const stats = ref<RuleStats | null>(null)
const matched = ref<TimeSeries | null>(null)
const actions = ref<TimeSeries | null>(null)
const loading = ref(true)
const error = ref<string | null>(null)

onMounted(async () => {
  try {
    const [rules, matchedSeries, actionSeries] = await Promise.all([
      statsApi.rules({}),
      statsApi.timeSeries({ metric: 'Matched', groupBy: 'none', ruleId: props.ruleId }),
      statsApi.timeSeries({ metric: 'actions', groupBy: 'action', ruleId: props.ruleId }),
    ])
    stats.value = rules.rules.find((r) => r.ruleId === props.ruleId) ?? null
    matched.value = matchedSeries
    actions.value = groupByAction(actionSeries)
  } catch (e) {
    error.value = describeRequestError(e).message
  } finally {
    loading.value = false
  }
})

const hasValues = (data: TimeSeries | null) => !!data && data.series.some((s) => s.values.some((v) => v > 0))
</script>

<template>
  <div class="grid gap-4" data-testid="rule-stats-panel">
    <p v-if="error" class="text-destructive text-sm" role="alert">{{ error }}</p>
    <div class="grid grid-cols-2 gap-3 sm:grid-cols-4">
      <KpiTile :label="$t('stats.ruleColumns.matched')" :value="stats?.matched ?? (loading ? null : 0)" />
      <KpiTile :label="$t('stats.ruleColumns.actions')" :value="stats?.actions ?? (loading ? null : 0)" />
      <KpiTile :label="$t('stats.ruleColumns.undone')" :value="stats?.undone ?? (loading ? null : 0)" />
      <KpiTile :label="$t('stats.dryRunActions')" :value="stats?.dryRunActions ?? (loading ? null : 0)" />
    </div>
    <p class="text-muted-foreground text-sm">
      {{
        stats?.lastMatch
          ? $t('stats.lastMatch', {
              date: new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeZone: 'UTC' }).format(
                new Date(`${stats.lastMatch}T00:00:00Z`),
              ),
            })
          : $t('stats.neverMatchedYet')
      }}
    </p>
    <ChartCard
      :title="$t('stats.ruleMatches')"
      :description="$t('stats.last30Days')"
      :loading="loading"
      :loaded="!loading"
      :empty="!hasValues(matched)"
      table
    >
      <TimeSeriesChart
        v-if="matched"
        :data="matched"
        kind="line"
        granularity="Day"
        :style-of="matchedStyle"
        :height="200"
      />
      <template #table
        ><SeriesTable v-if="matched" :data="matched" granularity="Day" :style-of="matchedStyle"
      /></template>
    </ChartCard>
    <ChartCard
      :title="$t('stats.byActionTitle')"
      :description="$t('stats.last30Days')"
      :loading="loading"
      :loaded="!loading"
      :empty="!actions || actions.series.length === 0"
      table
    >
      <TimeSeriesChart
        v-if="actions"
        :data="actions"
        kind="stack"
        granularity="Day"
        :style-of="groupStyle"
        :height="200"
      />
      <template #table
        ><SeriesTable v-if="actions" :data="actions" granularity="Day" :style-of="groupStyle"
      /></template>
    </ChartCard>
  </div>
</template>
