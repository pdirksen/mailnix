<script setup lang="ts">
import { Card, CardContent } from '@/components/ui/card'
import { ArrowDownRightIcon, ArrowUpRightIcon } from '@lucide/vue'
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterLink, type RouteLocationRaw } from 'vue-router'

/** One headline number, optionally compared with the previous period. */
const props = withDefaults(
  defineProps<{
    label: string
    value: number | null
    previous?: number
    /** Whether more is better (green up) or worse (green down); neutral without. */
    upIsGood?: boolean
    /** Name of the previous period, e.g. "yesterday". */
    previousLabel?: string
    to?: RouteLocationRaw
    testId?: string
  }>(),
  // Without this, Vue turns an omitted boolean into false ("up is bad") instead of "neutral".
  { upIsGood: undefined, previous: undefined, previousLabel: undefined, to: undefined, testId: undefined },
)

const { locale } = useI18n()
const formatted = computed(() =>
  props.value === null
    ? '–'
    : new Intl.NumberFormat(locale.value, {
        notation: props.value >= 10000 ? 'compact' : 'standard',
        maximumFractionDigits: 1,
      }).format(props.value),
)
const delta = computed(() =>
  props.previous === undefined || props.value === null ? null : props.value - props.previous,
)
const tone = computed(() => {
  if (!delta.value || props.upIsGood === undefined) return 'text-muted-foreground'
  return delta.value > 0 === props.upIsGood ? 'text-success' : 'text-destructive'
})
</script>

<template>
  <Card class="py-4" :data-testid="testId">
    <component
      :is="to ? RouterLink : 'div'"
      :to="to"
      class="block"
      :class="{ 'hover:bg-accent/40 -my-4 rounded-xl py-4 transition-colors': to }"
    >
      <CardContent class="grid gap-1">
        <span class="text-muted-foreground text-sm">{{ label }}</span>
        <span class="text-2xl font-semibold">{{ formatted }}</span>
        <span v-if="delta !== null" class="flex flex-wrap items-center gap-x-1 text-xs whitespace-nowrap" :class="tone">
          <ArrowUpRightIcon v-if="delta > 0" class="size-3.5" />
          <ArrowDownRightIcon v-else-if="delta < 0" class="size-3.5" />
          {{ delta > 0 ? '+' : '' }}{{ new Intl.NumberFormat(locale).format(delta) }}
          <span class="text-muted-foreground">{{ previousLabel }}</span>
        </span>
      </CardContent>
    </component>
  </Card>
</template>
