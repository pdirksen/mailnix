<script setup lang="ts">
import { Button } from '@/components/ui/button'
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from '@/components/ui/collapsible'
import { Skeleton } from '@/components/ui/skeleton'
import { useConnectionErrors } from '@/lib/connectionErrors'
import { formatDateTime, formatRelative } from '@/lib/format'
import { type RunDetail, type RunSummary, useRunsStore } from '@/stores/runs'
import { ChevronRightIcon, ShieldAlertIcon, Undo2Icon } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterLink } from 'vue-router'
import { toast } from 'vue-sonner'
import RunActionsTable from './RunActionsTable.vue'
import RunStateBadge from './RunStateBadge.vue'

const props = defineProps<{ run: RunSummary; initiallyOpen?: boolean }>()
const emit = defineEmits<{ undo: [run: RunSummary] }>()

const { locale } = useI18n()
const runs = useRunsStore()
const { describeRequestError } = useConnectionErrors()

const open = ref(props.initiallyOpen ?? false)
const detail = ref<RunDetail | null>(null)
const loading = ref(false)

async function load() {
  loading.value = true
  try {
    detail.value = await runs.fetchDetail(props.run.id)
  } catch (error) {
    toast.error(describeRequestError(error).message)
  } finally {
    loading.value = false
  }
}

// Reload the details while they are shown and the run changes (live updates).
watch(
  () => [open.value, props.run.state, props.run.executedActions, props.run.undoneByRunId] as const,
  ([isOpen]) => {
    if (isOpen) void load()
  },
  { immediate: true },
)

const isUndo = computed(() => props.run.trigger === 'Undo')
</script>

<template>
  <Collapsible v-model:open="open" as="li" class="bg-card rounded-lg border" :data-testid="`run-${run.id}`">
    <div class="flex flex-wrap items-center gap-3 px-3 py-3">
      <CollapsibleTrigger as-child>
        <Button variant="ghost" size="icon" class="size-7" :aria-label="$t('activity.details')">
          <ChevronRightIcon class="transition-transform" :class="{ 'rotate-90': open }" />
        </Button>
      </CollapsibleTrigger>
      <div class="min-w-0 flex-1">
        <div class="flex flex-wrap items-center gap-2">
          <span class="font-medium">{{ $t(`activity.triggers.${run.trigger}`) }}</span>
          <RunStateBadge :state="run.state" :undone="!!run.undoneByRunId" />
          <span class="text-muted-foreground text-sm">{{ run.accountName }} · {{ run.folder }}</span>
        </div>
        <p class="text-muted-foreground text-sm">
          <span :title="formatDateTime(run.createdAt, locale)">{{ formatRelative(run.createdAt, locale) }}</span>
          ·
          <template v-if="isUndo">{{ $t('activity.undoCounts', { executed: run.executedActions }) }}</template>
          <template v-else-if="run.trigger === 'Purge'">{{ $t('activity.purgeCounts', { executed: run.executedActions, examined: run.messagesExamined }) }}</template>
          <template v-else>
            {{ $t('activity.counts', { executed: run.executedActions, matched: run.messagesMatched, examined: run.messagesExamined }) }}
          </template>
          <span v-if="run.failedActions" class="text-destructive"> · {{ $t('activity.countsFailed', { failed: run.failedActions }) }}</span>
        </p>
        <p v-if="run.error" class="text-destructive text-sm">{{ run.error }}</p>
      </div>
      <Button v-if="run.state === 'AwaitingConfirmation'" size="sm" as-child>
        <RouterLink :to="{ name: 'run-review', params: { id: run.id } }" data-testid="review-run">
          <ShieldAlertIcon />
          {{ $t('activity.review') }}
        </RouterLink>
      </Button>
      <Button v-if="run.canUndo" size="sm" variant="outline" data-testid="undo-run" @click="emit('undo', run)">
        <Undo2Icon />
        {{ $t('activity.undo') }}
      </Button>
    </div>
    <CollapsibleContent class="grid gap-2 border-t px-3 py-3">
      <p v-if="run.searchFilter" class="text-muted-foreground text-xs" data-testid="search-filter">
        {{ $t('activity.searchFilter') }} <code class="bg-muted rounded px-1 py-0.5 break-all">{{ run.searchFilter }}</code>
      </p>
      <Skeleton v-if="loading && !detail" class="h-20" />
      <RunActionsTable v-else-if="detail" :actions="detail.actions" />
    </CollapsibleContent>
  </Collapsible>
</template>
