<script setup lang="ts">
import type { Schemas } from '@/api/client'
import { chrome, compact, useDarkMode } from '@/lib/charts'
import type { EChartsOption } from 'echarts'
import { computed } from 'vue'
import VChart from 'vue-echarts'
import { useI18n } from 'vue-i18n'
import { formatBucket, type Granularity } from './buckets'

export type TimeSeries = Schemas['TimeSeries']

export interface SeriesStyle {
  label: string
  color: string
}

const props = defineProps<{
  data: TimeSeries
  /** Stacked columns (counts per day that add up) or lines (trends). */
  kind: 'stack' | 'line'
  granularity: Granularity
  /** Label and color per series key; colors belong to the thing, never to its rank. */
  styleOf: (key: string, name: string | null | undefined) => SeriesStyle
  height?: number
}>()

const emit = defineEmits<{ select: [bucket: string, key: string] }>()

const { locale } = useI18n()
const dark = useDarkMode()

function escape(text: string) {
  return text.replace(/[&<>"']/g, (c) => `&#${c.charCodeAt(0)};`)
}

const option = computed<EChartsOption>(() => {
  const c = chrome(dark.value)
  const series = props.data.series.map((s) => ({ ...s, style: props.styleOf(s.key, s.name) }))
  const number = new Intl.NumberFormat(locale.value)
  return {
    animation: false,
    grid: { left: 4, right: 12, top: series.length > 1 ? 36 : 12, bottom: 4, containLabel: true },
    legend:
      series.length > 1
        ? {
            top: 0,
            left: 0,
            itemWidth: props.kind === 'line' ? 16 : 10,
            itemHeight: props.kind === 'line' ? 2 : 10,
            itemGap: 16,
            icon: props.kind === 'line' ? 'rect' : 'roundRect',
            // The series' surface ring (for dots) would hide a 2px legend key.
            itemStyle: { borderWidth: 0 },
            textStyle: { color: c.muted },
          }
        : undefined,
    tooltip: {
      trigger: 'axis',
      axisPointer:
        props.kind === 'line'
          ? { type: 'line', lineStyle: { color: c.axis } }
          : { type: 'shadow', shadowStyle: { color: dark.value ? 'rgba(255,255,255,0.04)' : 'rgba(0,0,0,0.03)' } },
      backgroundColor: c.tooltip,
      borderColor: c.border,
      textStyle: { color: c.text },
      // Values lead, names follow; names are user data, hence escaped.
      formatter: (params: unknown) => {
        const rows = params as { seriesIndex: number; value: number; axisValueLabel: string }[]
        const head = `<div style="color:${c.muted};margin-bottom:4px">${escape(rows[0]?.axisValueLabel ?? '')}</div>`
        return (
          head +
          rows
            .map((row) => {
              const s = series[row.seriesIndex]!
              return `<div style="display:flex;align-items:center;gap:8px"><span style="width:12px;height:2px;background:${s.style.color}"></span><strong>${number.format(row.value)}</strong><span style="color:${c.muted}">${escape(s.style.label)}</span></div>`
            })
            .join('')
        )
      },
    },
    xAxis: {
      type: 'category',
      data: props.data.buckets.map((b) => formatBucket(b, props.granularity, locale.value)),
      axisLine: { lineStyle: { color: c.axis } },
      axisTick: { show: false },
      axisLabel: { color: c.muted, hideOverlap: true },
    },
    yAxis: {
      type: 'value',
      minInterval: 1,
      splitLine: { lineStyle: { color: c.grid, width: 1 } },
      axisLabel: { color: c.muted, formatter: (value: number) => compact(value, locale.value) },
    },
    series: series.map((s, index) =>
      props.kind === 'stack'
        ? {
            type: 'bar' as const,
            stack: 'total',
            name: s.style.label,
            data: [...s.values],
            barMaxWidth: 24,
            cursor: 'pointer',
            // A 2px surface gap between stacked segments; the top of the column is rounded.
            itemStyle: {
              color: s.style.color,
              borderColor: c.surface,
              borderWidth: 1,
              borderRadius: index === series.length - 1 ? [4, 4, 0, 0] : 0,
            },
          }
        : {
            type: 'line' as const,
            name: s.style.label,
            data: [...s.values],
            symbol: 'circle',
            symbolSize: 8,
            showSymbol: false,
            cursor: 'pointer',
            lineStyle: { width: 2, color: s.style.color, cap: 'round', join: 'round' },
            itemStyle: { color: s.style.color, borderColor: c.surface, borderWidth: 2 },
            areaStyle: series.length === 1 ? { color: s.style.color, opacity: 0.1 } : undefined,
          },
    ),
  }
})

function onClick(params: { dataIndex: number; seriesIndex?: number }) {
  const bucket = props.data.buckets[params.dataIndex]
  const series = props.data.series[params.seriesIndex ?? 0]
  if (bucket && series) emit('select', bucket, series.key)
}
</script>

<template>
  <VChart :option="option" autoresize :style="{ height: `${height ?? 260}px` }" role="img" @click="onClick" />
</template>
