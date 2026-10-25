<script setup lang="ts">
import { slotColor, useDarkMode } from '@/lib/charts'
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterLink, type RouteLocationRaw } from 'vue-router'

export interface BarListItem {
  key: string
  label: string
  value: number
  /** Secondary text after the value, e.g. "of 120". */
  detail?: string
  to?: RouteLocationRaw
}

/** A ranking: one bar per row in a single color, with the value written out (no hovering needed). */
const props = defineProps<{ items: BarListItem[] }>()

const { locale } = useI18n()
const dark = useDarkMode()
const max = computed(() => Math.max(1, ...props.items.map((i) => i.value)))
const number = computed(() => new Intl.NumberFormat(locale.value))
</script>

<template>
  <ul class="grid gap-2" role="list">
    <li v-for="item in items" :key="item.key" class="grid gap-1">
      <div class="flex items-baseline justify-between gap-3 text-sm">
        <component
          :is="item.to ? RouterLink : 'span'"
          :to="item.to"
          class="min-w-0 truncate"
          :class="{ 'hover:underline': item.to }"
          >{{ item.label }}</component
        >
        <span class="shrink-0 tabular-nums">
          <strong>{{ number.format(item.value) }}</strong>
          <span v-if="item.detail" class="text-muted-foreground ml-1">{{ item.detail }}</span>
        </span>
      </div>
      <div class="h-2 w-full">
        <div
          class="h-2 rounded-r-[4px]"
          :style="{
            width: `${(item.value / max) * 100}%`,
            minWidth: item.value > 0 ? '4px' : '0',
            background: slotColor(0, dark),
          }"
        />
      </div>
    </li>
  </ul>
</template>
