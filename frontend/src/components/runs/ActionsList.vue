<script setup lang="ts">
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from '@/components/ui/alert-dialog'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Skeleton } from '@/components/ui/skeleton'
import type { ActionType } from '@/lib/charts'
import { useConnectionErrors } from '@/lib/connectionErrors'
import { formatDateTime, formatRelative } from '@/lib/format'
import { type ActionEntry, statsApi } from '@/lib/stats'
import { describeAction } from '@/rules/describe'
import { useRunsStore } from '@/stores/runs'
import { Loader2Icon, Undo2Icon } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterLink } from 'vue-router'
import { toast } from 'vue-sonner'

export interface ActionFilters {
  accountId?: string
  ruleId?: string
  action?: ActionType
  from?: string
  to?: string
  failedOnly?: boolean
}

const props = defineProps<{
  filters: ActionFilters
  /** Entries per page. */
  limit?: number
  /** Allow selecting entries and undoing the selection. */
  selectable?: boolean
  /** Short form for the dashboard: no paging, no selection. */
  compact?: boolean
  /** Changing it reloads the list (e.g. after live updates). */
  refreshKey?: unknown
}>()

const { t, locale } = useI18n()
const runs = useRunsStore()
const { describeRequestError } = useConnectionErrors()

const items = ref<ActionEntry[]>([])
const next = ref<string | null>(null)
const loading = ref(false)
const loaded = ref(false)
const error = ref<string | null>(null)
const selected = ref(new Set<string>())

async function load(more = false) {
  loading.value = true
  error.value = null
  try {
    const page = await statsApi.actions({
      ...props.filters,
      limit: props.limit ?? 50,
      before: more ? (next.value ?? undefined) : undefined,
    })
    items.value = more ? [...items.value, ...page.items] : page.items
    next.value = page.nextBefore
    if (!more)
      selected.value = new Set([...selected.value].filter((id) => page.items.some((i) => i.id === id && i.canUndo)))
    loaded.value = true
  } catch (e) {
    error.value = describeRequestError(e).message
  } finally {
    loading.value = false
  }
}

watch(
  () => [JSON.stringify(props.filters), props.refreshKey],
  () => void load(),
  { immediate: true },
)

function toggle(entry: ActionEntry, value: boolean | 'indeterminate') {
  const nextSet = new Set(selected.value)
  if (value === true) nextSet.add(entry.id)
  else nextSet.delete(entry.id)
  selected.value = nextSet
}

const undoable = computed(() => items.value.filter((i) => i.canUndo))
const allSelected = computed(() => undoable.value.length > 0 && undoable.value.every((i) => selected.value.has(i.id)))

function toggleAll(value: boolean | 'indeterminate') {
  selected.value = value === true ? new Set(undoable.value.map((i) => i.id)) : new Set()
}

const confirmOpen = ref(false)
const undoing = ref(false)

async function undoSelected() {
  undoing.value = true
  try {
    for (const run of await statsApi.undoActions([...selected.value])) runs.upsert(run)
    toast.success(t('activity.undoStarted'))
    selected.value = new Set()
  } catch (e) {
    toast.error(describeRequestError(e).message)
  } finally {
    undoing.value = false
  }
}

function label(entry: ActionEntry) {
  return describeAction({ type: entry.action, targetFolder: entry.targetFolder, keyword: entry.keyword }, t)
}
</script>

<template>
  <div class="grid gap-2">
    <div v-if="selectable && undoable.length > 0" class="flex flex-wrap items-center gap-3 text-sm">
      <label class="flex items-center gap-2">
        <Checkbox :model-value="allSelected" :aria-label="$t('activity.selectAll')" @update:model-value="toggleAll" />
        {{ $t('activity.selectAll') }}
      </label>
      <Button
        v-if="selected.size > 0"
        size="sm"
        variant="outline"
        :disabled="undoing"
        data-testid="undo-selected"
        @click="confirmOpen = true"
      >
        <Loader2Icon v-if="undoing" class="animate-spin" />
        <Undo2Icon v-else />
        {{ $t('activity.undoSelected', { count: selected.size }, selected.size) }}
      </Button>
    </div>

    <div v-if="loading && !loaded" class="grid gap-2">
      <Skeleton v-for="i in 3" :key="i" class="h-12" />
    </div>
    <p v-else-if="error" class="text-destructive text-sm" role="alert">{{ error }}</p>
    <p v-else-if="items.length === 0" class="text-muted-foreground py-6 text-center text-sm">
      {{ $t('activity.noActions') }}
    </p>
    <ul v-else class="divide-y rounded-lg border" :class="{ 'opacity-60': loading }" data-testid="action-list">
      <li
        v-for="entry in items"
        :key="entry.id"
        class="flex items-start gap-3 px-3 py-2"
        :data-testid="`action-entry-${entry.action}`"
      >
        <!-- Side by side from small screens up; stacked on phones so the subject keeps its room. -->
        <Checkbox
          v-if="selectable"
          class="mt-1"
          :model-value="selected.has(entry.id)"
          :disabled="!entry.canUndo"
          :aria-label="$t('activity.select')"
          @update:model-value="(value) => toggle(entry, value)"
        />
        <div class="flex min-w-0 flex-1 flex-col gap-1 sm:flex-row sm:items-start sm:gap-3">
          <div class="min-w-0 flex-1">
            <p class="truncate text-sm font-medium">{{ entry.subject || '—' }}</p>
            <p class="text-muted-foreground truncate text-xs">
              {{ entry.from }}
              <template v-if="!compact"> · {{ entry.accountName }} · {{ entry.sourceFolder }}</template>
            </p>
          </div>
          <div class="grid gap-1 sm:shrink-0 sm:justify-items-end sm:text-right">
            <span class="text-sm">{{ label(entry) }}</span>
            <span class="text-muted-foreground flex flex-wrap gap-1 text-xs sm:justify-end">
              <RouterLink
                v-if="entry.ruleId && !compact"
                :to="{ name: 'rule-edit', params: { id: entry.ruleId } }"
                class="hover:underline"
                >{{ entry.ruleName }}</RouterLink
              >
              <span v-else-if="entry.ruleName">{{ entry.ruleName }}</span>
              <span :title="formatDateTime(entry.createdAt, locale)">{{
                formatRelative(entry.createdAt, locale)
              }}</span>
            </span>
            <span class="flex gap-1 sm:justify-end">
              <Badge v-if="entry.dryRun" variant="outline">{{ $t('activity.dryRun') }}</Badge>
              <Badge
                v-if="!entry.success"
                variant="outline"
                class="border-destructive/50 text-destructive"
                :title="entry.error ?? undefined"
                >{{ $t('activity.failed') }}</Badge
              >
              <Badge v-if="entry.undoneAt" variant="outline" class="text-muted-foreground">{{
                $t('activity.undone')
              }}</Badge>
            </span>
          </div>
        </div>
      </li>
    </ul>

    <Button
      v-if="next && !compact"
      variant="outline"
      class="justify-self-center"
      :disabled="loading"
      data-testid="load-more"
      @click="load(true)"
    >
      <Loader2Icon v-if="loading" class="animate-spin" />
      {{ $t('activity.loadMore') }}
    </Button>

    <AlertDialog v-model:open="confirmOpen">
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>{{
            $t('activity.undoSelectedTitle', { count: selected.size }, selected.size)
          }}</AlertDialogTitle>
          <AlertDialogDescription>{{ $t('activity.undoSelectedText') }}</AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel>{{ $t('common.cancel') }}</AlertDialogCancel>
          <AlertDialogAction data-testid="confirm-undo-selected" @click="undoSelected">{{
            $t('activity.undo')
          }}</AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  </div>
</template>
