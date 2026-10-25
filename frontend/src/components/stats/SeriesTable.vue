<script setup lang="ts">
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { formatBucket, type Granularity } from './buckets'
import type { SeriesStyle, TimeSeries } from './TimeSeriesChart.vue'

/** The table twin of a chart: every value readable without hovering or telling colors apart. */
const props = defineProps<{
  data: TimeSeries
  granularity: Granularity
  styleOf: (key: string, name: string | null | undefined) => SeriesStyle
}>()

const { t, locale } = useI18n()
const number = computed(() => new Intl.NumberFormat(locale.value))
const columns = computed(() =>
  props.data.series.map((s) => ({ key: s.key, label: props.styleOf(s.key, s.name).label, values: s.values })),
)
// Newest first, empty buckets left out.
const rows = computed(() =>
  props.data.buckets
    .map((bucket, i) => ({ bucket, values: columns.value.map((c) => c.values[i] ?? 0) }))
    .filter((row) => row.values.some((v) => v > 0))
    .reverse(),
)
</script>

<template>
  <p v-if="rows.length === 0" class="text-muted-foreground py-6 text-center text-sm">{{ t('stats.noData') }}</p>
  <div v-else class="max-h-72 overflow-auto">
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead>{{ t('stats.period') }}</TableHead>
          <TableHead v-for="column in columns" :key="column.key" class="text-right">{{ column.label }}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        <TableRow v-for="row in rows" :key="row.bucket">
          <TableCell class="whitespace-nowrap">{{ formatBucket(row.bucket, granularity, locale) }}</TableCell>
          <TableCell v-for="(value, i) in row.values" :key="i" class="text-right tabular-nums">{{
            number.format(value)
          }}</TableCell>
        </TableRow>
      </TableBody>
    </Table>
  </div>
</template>
