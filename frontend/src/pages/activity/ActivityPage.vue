<script setup lang="ts">
import ActionsList, { type ActionFilters } from '@/components/runs/ActionsList.vue'
import RunItem from '@/components/runs/RunItem.vue'
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
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs'
import type { ActionType } from '@/lib/charts'
import { useRulesStore } from '@/stores/rules'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { useConnectionErrors } from '@/lib/connectionErrors'
import { useAccountsStore } from '@/stores/accounts'
import { type RunState, type RunSummary, useRunsStore } from '@/stores/runs'
import { HistoryIcon, RefreshCwIcon } from '@lucide/vue'
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { toast } from 'vue-sonner'

const ALL = 'all'
const STATES: RunState[] = ['Running', 'AwaitingConfirmation', 'Completed', 'CompletedWithErrors', 'Failed', 'Cancelled', 'Expired']

const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const runs = useRunsStore()
const accounts = useAccountsStore()
const { describeRequestError } = useConnectionErrors()

const loading = ref(!runs.loaded)

async function refresh() {
  actionsKey.value++
  loading.value = true
  try {
    await Promise.all([runs.fetchRecent(200), accounts.loaded ? Promise.resolve() : accounts.fetchAll()])
  } catch (error) {
    toast.error(describeRequestError(error).message)
  } finally {
    loading.value = false
  }
}

onMounted(() => {
  void refresh()
  void rules.fetchAll().catch(() => {})
})

function queryFilter(name: string) {
  return computed({
    get: () => (typeof route.query[name] === 'string' ? (route.query[name] as string) : ALL),
    set: (value: string) => void router.replace({ query: { ...route.query, [name]: value === ALL ? undefined : value } }),
  })
}

const accountFilter = queryFilter('account')
const stateFilter = queryFilter('state')

// Runs (what happened when) or single actions (what happened to which message).
const view = computed({
  get: () => (route.query.view === 'actions' ? 'actions' : 'runs'),
  set: (value: string) => void router.replace({ query: { ...route.query, view: value === 'actions' ? 'actions' : undefined } }),
})
const ACTIONS: ActionType[] = ['MoveTo', 'Delete', 'Archive', 'MarkAsSpam', 'CopyTo', 'Flag', 'Unflag', 'MarkRead', 'MarkUnread', 'AddKeyword', 'RemoveKeyword', 'Purge']
const actionFilter = queryFilter('action')
const ruleFilter = queryFilter('rule')
const rules = useRulesStore()

function queryText(name: string) {
  return computed({
    get: () => (typeof route.query[name] === 'string' ? (route.query[name] as string) : ''),
    set: (value: string) => void router.replace({ query: { ...route.query, [name]: value || undefined } }),
  })
}
const fromFilter = queryText('from')
const toFilter = queryText('to')
const failedOnly = computed({
  get: () => route.query.failed === 'true',
  set: (value: boolean) => void router.replace({ query: { ...route.query, failed: value ? 'true' : undefined } }),
})
const actionFilters = computed<ActionFilters>(() => ({
  accountId: accountFilter.value === ALL ? undefined : accountFilter.value,
  ruleId: ruleFilter.value === ALL ? undefined : ruleFilter.value,
  action: actionFilter.value === ALL ? undefined : (actionFilter.value as ActionType),
  from: fromFilter.value || undefined,
  to: toFilter.value || undefined,
  failedOnly: failedOnly.value || undefined,
}))
const actionsKey = ref(0)
const highlighted = computed(() => (typeof route.query.run === 'string' ? route.query.run : undefined))

const visible = computed(() =>
  runs.runs.filter(
    (run) =>
      (accountFilter.value === ALL || run.accountId === accountFilter.value) &&
      (stateFilter.value === ALL || run.state === stateFilter.value) &&
      // Runs that found nothing to do are noise; keep them only while they are still going on.
      (run.trigger !== 'NewMail' || run.messagesMatched > 0 || run.state === 'Running' || run.state === 'Failed'),
  ),
)

// The dialog closes before its confirm handler runs, so open state and target are kept separately.
const undoing = ref<RunSummary | null>(null)
const undoDialogOpen = ref(false)

function askUndo(run: RunSummary) {
  undoing.value = run
  undoDialogOpen.value = true
}

async function confirmUndo() {
  const run = undoing.value
  if (!run) return
  try {
    await runs.undo(run.id)
    toast.success(t('activity.undoStarted'))
  } catch (error) {
    toast.error(describeRequestError(error).message)
  }
}
</script>

<template>
  <div class="mx-auto grid max-w-5xl gap-6">
    <div class="flex flex-wrap items-end justify-between gap-4">
      <div class="max-w-2xl">
        <h2 class="text-2xl font-semibold tracking-tight">{{ $t('activity.title') }}</h2>
        <p class="text-muted-foreground mt-1">{{ $t('activity.subtitle') }}</p>
      </div>
      <Button variant="outline" :disabled="loading" @click="refresh">
        <RefreshCwIcon :class="{ 'animate-spin': loading }" />
        {{ $t('common.refresh') }}
      </Button>
    </div>

    <Tabs v-model="view">
      <TabsList>
        <TabsTrigger value="runs" data-testid="view-runs">{{ $t('activity.views.runs') }}</TabsTrigger>
        <TabsTrigger value="actions" data-testid="view-actions">{{ $t('activity.views.actions') }}</TabsTrigger>
      </TabsList>
    </Tabs>

    <div class="flex flex-wrap items-center gap-4">
      <div class="flex items-center gap-2">
        <Label for="activity-account">{{ $t('activity.account') }}</Label>
        <Select v-model="accountFilter">
          <SelectTrigger id="activity-account" class="w-56"><SelectValue /></SelectTrigger>
          <SelectContent>
            <SelectItem :value="ALL">{{ $t('activity.allAccounts') }}</SelectItem>
            <SelectItem v-for="account in accounts.accounts" :key="account.id" :value="account.id">{{ account.name }}</SelectItem>
          </SelectContent>
        </Select>
      </div>
      <div v-if="view === 'runs'" class="flex items-center gap-2">
        <Label for="activity-state">{{ $t('activity.state') }}</Label>
        <Select v-model="stateFilter">
          <SelectTrigger id="activity-state" class="w-56"><SelectValue /></SelectTrigger>
          <SelectContent>
            <SelectItem :value="ALL">{{ $t('activity.allStates') }}</SelectItem>
            <SelectItem v-for="state in STATES" :key="state" :value="state">{{ $t(`activity.states.${state}`) }}</SelectItem>
          </SelectContent>
        </Select>
      </div>
      <template v-if="view === 'actions'">
        <div class="flex items-center gap-2">
          <Label for="activity-action">{{ $t('activity.action') }}</Label>
          <Select v-model="actionFilter">
            <SelectTrigger id="activity-action" class="w-48"><SelectValue /></SelectTrigger>
            <SelectContent>
              <SelectItem :value="ALL">{{ $t('activity.allActions') }}</SelectItem>
              <SelectItem v-for="action in ACTIONS" :key="action" :value="action">{{ $t(`rules.actions.${action}`) }}</SelectItem>
            </SelectContent>
          </Select>
        </div>
        <div v-if="ruleFilter !== ALL" class="flex items-center gap-2">
          <Label for="activity-rule">{{ $t('activity.rule') }}</Label>
          <Select v-model="ruleFilter">
            <SelectTrigger id="activity-rule" class="w-48"><SelectValue /></SelectTrigger>
            <SelectContent>
              <SelectItem :value="ALL">{{ $t('activity.allRules') }}</SelectItem>
              <SelectItem v-for="rule in rules.rules" :key="rule.id" :value="rule.id">{{ rule.name }}</SelectItem>
            </SelectContent>
          </Select>
        </div>
        <div class="flex items-center gap-2">
          <Label for="activity-from">{{ $t('activity.from') }}</Label>
          <Input id="activity-from" v-model="fromFilter" type="date" class="w-40" />
          <Label for="activity-to">{{ $t('activity.to') }}</Label>
          <Input id="activity-to" v-model="toFilter" type="date" class="w-40" />
        </div>
        <label class="flex items-center gap-2 text-sm">
          <Checkbox v-model="failedOnly" />
          {{ $t('activity.failedOnly') }}
        </label>
      </template>
    </div>

    <ActionsList v-if="view === 'actions'" :filters="actionFilters" selectable :refresh-key="actionsKey" />

    <div v-else-if="loading && !runs.loaded" class="grid gap-2">
      <Skeleton v-for="i in 3" :key="i" class="h-16" />
    </div>

    <Card v-else-if="visible.length === 0" class="py-10 text-center">
      <CardContent class="grid justify-items-center gap-3">
        <HistoryIcon class="text-muted-foreground size-8" />
        <p class="font-medium">{{ $t('activity.empty') }}</p>
        <p class="text-muted-foreground text-sm">{{ $t('activity.emptyHint') }}</p>
      </CardContent>
    </Card>

    <ul v-else class="grid gap-2" data-testid="run-list">
      <RunItem v-for="run in visible" :key="run.id" :run="run" :initially-open="run.id === highlighted" @undo="askUndo" />
    </ul>

    <AlertDialog v-model:open="undoDialogOpen">
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>{{ $t('activity.undoTitle') }}</AlertDialogTitle>
          <AlertDialogDescription>{{ $t('activity.undoText') }}</AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel>{{ $t('common.cancel') }}</AlertDialogCancel>
          <AlertDialogAction data-testid="confirm-undo" @click="confirmUndo">{{ $t('activity.undo') }}</AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  </div>
</template>
