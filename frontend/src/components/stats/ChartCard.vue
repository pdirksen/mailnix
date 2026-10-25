<script setup lang="ts">
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { DownloadIcon } from '@lucide/vue'
import { ref } from 'vue'

/**
 * Frame for one stats section: first load shows a placeholder, later reloads keep the previous content (dimmed),
 * and a chart can switch to its table twin.
 */
defineProps<{
  title: string
  description?: string
  loading?: boolean
  /** Data has been loaded at least once. */
  loaded?: boolean
  error?: string | null
  empty?: boolean
  /** CSV download of the section. */
  exportHref?: string
  /** Offer the chart/table switch. */
  table?: boolean
}>()

const view = ref<'chart' | 'table'>('chart')
</script>

<template>
  <Card class="gap-4">
    <CardHeader class="flex flex-row items-start justify-between gap-2">
      <div class="grid min-w-0 flex-1 gap-1">
        <CardTitle class="text-base">{{ title }}</CardTitle>
        <CardDescription v-if="description">{{ description }}</CardDescription>
      </div>
      <div class="flex shrink-0 items-center gap-1">
        <div
          v-if="table && loaded && !empty"
          class="bg-muted flex rounded-md p-0.5 text-xs"
          role="group"
          :aria-label="$t('stats.view')"
        >
          <button
            v-for="option in ['chart', 'table'] as const"
            :key="option"
            type="button"
            class="rounded px-2 py-1"
            :class="view === option ? 'bg-background shadow-sm' : 'text-muted-foreground'"
            :aria-pressed="view === option"
            @click="view = option"
          >
            {{ $t(`stats.views.${option}`) }}
          </button>
        </div>
        <Button v-if="exportHref" variant="ghost" size="icon" as-child :title="$t('stats.export')">
          <a :href="exportHref" download :aria-label="$t('stats.export')"><DownloadIcon /></a>
        </Button>
      </div>
    </CardHeader>
    <CardContent>
      <Skeleton v-if="loading && !loaded" class="h-56" />
      <p v-else-if="error" class="text-destructive text-sm" role="alert">{{ error }}</p>
      <p v-else-if="empty" class="text-muted-foreground py-10 text-center text-sm">{{ $t('stats.noData') }}</p>
      <div v-else :class="{ 'opacity-60 transition-opacity': loading }">
        <slot v-if="view === 'table' && table" name="table" />
        <slot v-else />
      </div>
    </CardContent>
  </Card>
</template>
