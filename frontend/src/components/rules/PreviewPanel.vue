<script setup lang="ts">
import ConnectionErrorAlert from '@/components/accounts/ConnectionErrorAlert.vue'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { type DescribedError, useConnectionErrors } from '@/lib/connectionErrors'
import { formatDateTime, formatRelative } from '@/lib/format'
import { describeAction } from '@/rules/describe'
import { type Preview, type PreviewMessage, type RuleDraft, useRulesStore } from '@/stores/rules'
import { CheckIcon, ChevronDownIcon, ChevronRightIcon, Loader2Icon, PlayIcon, RefreshCwIcon, TriangleAlertIcon } from '@lucide/vue'
import { useDebounceFn } from '@vueuse/core'
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import ConditionTrace from './ConditionTrace.vue'

const props = defineProps<{
  accountId: string
  ruleId?: string
  draft: RuleDraft
  /** The draft passes validation; previews only run for valid drafts. */
  valid: boolean
  folders: string[]
  defaultFolder: string
}>()

const { t, locale } = useI18n()
const store = useRulesStore()
const { describeRequestError } = useConnectionErrors()

const folder = ref(props.defaultFolder)
watch(() => props.defaultFolder, (value) => (folder.value ||= value))
const limit = ref('100')
const LIMITS = ['50', '100', '200', '500']

const preview = ref<Preview | null>(null)
const running = ref(false)
const error = ref<DescribedError | null>(null)
const filter = ref<'matches' | 'all'>('matches')
const expanded = ref(new Set<number>())

async function run(refresh = false) {
  if (!props.valid) return
  running.value = true
  error.value = null
  try {
    preview.value = await store.preview(props.accountId, props.draft, {
      ruleId: props.ruleId,
      folder: folder.value || undefined,
      limit: Number(limit.value),
      refresh,
    })
  } catch (e) {
    error.value = describeRequestError(e)
  } finally {
    running.value = false
  }
}

// After the first run, keep the preview in sync with the rule (messages come from the server's cache).
const rerun = useDebounceFn(() => run(false), 800)
watch(
  () => [JSON.stringify(props.draft.conditions), JSON.stringify(props.draft.actions), props.draft.stopProcessing, props.valid],
  () => {
    if (preview.value && props.valid) void rerun()
  },
)
watch([folder, limit], () => {
  if (preview.value) void run(false)
})

const messages = computed(() => (preview.value?.messages ?? []).filter((m) => filter.value === 'all' || m.matched))

function toggle(uid: number) {
  const next = new Set(expanded.value)
  if (next.has(uid)) next.delete(uid)
  else next.add(uid)
  expanded.value = next
}

function actionLabel(action: { type: string; targetFolder?: string | null; keyword?: string | null }) {
  return describeAction(action, t)
}

function skipLabel(skip: PreviewMessage['skipped'][number]) {
  return `${t(`rules.actions.${skip.type}`)} – ${t(`rules.skipReasons.${skip.reason}`, { rule: skip.blockedByRuleName ?? '' })}`
}
</script>

<template>
  <Card data-testid="preview-panel">
    <CardHeader>
      <CardTitle>{{ $t('rules.preview.title') }}</CardTitle>
      <CardDescription>{{ $t('rules.preview.hint') }}</CardDescription>
    </CardHeader>
    <CardContent class="grid gap-4">
      <div class="flex flex-wrap items-end gap-3">
        <div class="grid gap-1.5">
          <Label for="preview-folder">{{ $t('rules.preview.folder') }}</Label>
          <Select v-model="folder">
            <SelectTrigger id="preview-folder" class="w-56">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem v-for="f in folders" :key="f" :value="f">{{ f }}</SelectItem>
            </SelectContent>
          </Select>
        </div>
        <div class="grid gap-1.5">
          <Label for="preview-limit">{{ $t('rules.preview.limit') }}</Label>
          <Select v-model="limit">
            <SelectTrigger id="preview-limit" class="w-24">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem v-for="l in LIMITS" :key="l" :value="l">{{ l }}</SelectItem>
            </SelectContent>
          </Select>
        </div>
        <Button :disabled="!valid || running" data-testid="run-preview" @click="run(!!preview)">
          <Loader2Icon v-if="running" class="animate-spin" />
          <RefreshCwIcon v-else-if="preview" />
          <PlayIcon v-else />
          {{ preview ? $t('rules.preview.refresh') : $t('rules.preview.run') }}
        </Button>
      </div>

      <p v-if="!valid" class="text-muted-foreground text-sm">{{ $t('rules.preview.invalid') }}</p>
      <p v-if="running && !preview" class="text-muted-foreground flex items-center gap-2 text-sm" role="status">
        <Loader2Icon class="size-4 animate-spin" />
        {{ $t('rules.preview.running') }}
      </p>
      <ConnectionErrorAlert v-if="error" :title="$t('accounts.testFailed')" :error="error" />

      <template v-if="preview">
        <div class="flex flex-wrap items-center justify-between gap-3">
          <p class="text-sm" data-testid="preview-summary">
            <span class="font-medium">{{ $t('rules.preview.summary', { matched: preview.matched, examined: preview.examined }) }}</span>
            <span v-if="preview.terminalActions" class="text-muted-foreground"> · {{ $t('rules.preview.terminal', { count: preview.terminalActions }) }}</span>
            <span class="text-muted-foreground block text-xs" :title="formatDateTime(preview.fetchedAt, locale)">
              {{ $t('rules.preview.fromCache', { time: formatRelative(preview.fetchedAt, locale) }) }}
            </span>
          </p>
          <Tabs v-model="filter">
            <TabsList>
              <TabsTrigger value="matches">{{ $t('rules.preview.onlyMatches') }}</TabsTrigger>
              <TabsTrigger value="all">{{ $t('rules.preview.all') }}</TabsTrigger>
            </TabsList>
          </Tabs>
        </div>

        <Alert v-if="preview.exceedsConfirmationThreshold" data-testid="preview-threshold">
          <TriangleAlertIcon />
          <AlertDescription>{{ $t('rules.preview.threshold', { threshold: preview.confirmationThreshold }) }}</AlertDescription>
        </Alert>
        <p v-if="preview.limit < Number(limit)" class="text-muted-foreground text-xs">{{ $t('rules.preview.limitReduced', { limit: preview.limit }) }}</p>

        <p v-if="messages.length === 0" class="text-muted-foreground text-sm">{{ $t('rules.preview.noMessages') }}</p>
        <!-- Results of the last valid version stay visible but dimmed while the rule is incomplete. -->
        <ul v-else class="divide-y rounded-md border transition-opacity" :class="{ 'opacity-50': !valid }" role="list" data-testid="preview-messages">
          <li v-for="message in messages" :key="message.uid">
            <button
              type="button"
              class="hover:bg-accent/50 grid w-full grid-cols-[1.25rem_1fr] items-start gap-2 px-3 py-2 text-left"
              :aria-expanded="expanded.has(message.uid)"
              @click="toggle(message.uid)"
            >
              <span class="mt-0.5">
                <ChevronDownIcon v-if="expanded.has(message.uid)" class="size-4" />
                <ChevronRightIcon v-else class="size-4" />
              </span>
              <span class="grid min-w-0 gap-1">
                <span class="flex min-w-0 items-center gap-2">
                  <CheckIcon v-if="message.matched" class="text-success size-4 shrink-0" :aria-label="$t('rules.preview.matched')" />
                  <span class="truncate text-sm" :class="{ 'font-semibold': !message.isSeen }">{{ message.subject || $t('rules.preview.noSubject') }}</span>
                </span>
                <span class="text-muted-foreground truncate text-xs">
                  {{ message.fromName || message.from }}<template v-if="message.date"> · {{ formatDateTime(message.date, locale) }}</template>
                </span>
                <span v-if="message.actions.length || message.skipped.length" class="flex flex-wrap gap-1">
                  <Badge v-for="(action, i) in message.actions" :key="`a${i}`" variant="secondary" class="max-w-full truncate">{{ actionLabel(action) }}</Badge>
                  <Badge v-for="(skip, i) in message.skipped" :key="`s${i}`" variant="outline" class="text-muted-foreground max-w-full truncate line-through">
                    {{ actionLabel(skip) }}
                  </Badge>
                </span>
              </span>
            </button>
            <div v-if="expanded.has(message.uid)" class="bg-muted/30 grid gap-3 px-9 py-3" data-testid="preview-details">
              <div>
                <p class="text-muted-foreground mb-1 text-xs font-medium uppercase">{{ $t('rules.preview.why') }}</p>
                <ConditionTrace v-if="message.trace" :node="message.trace" />
              </div>
              <div v-if="message.skipped.length">
                <p class="text-muted-foreground mb-1 text-xs font-medium uppercase">{{ $t('rules.preview.skipped') }}</p>
                <p v-for="(skip, i) in message.skipped" :key="i" class="text-sm">{{ skipLabel(skip) }}</p>
              </div>
              <div v-if="message.otherRulesActions.length">
                <p class="text-muted-foreground mb-1 text-xs font-medium uppercase">{{ $t('rules.preview.otherRules') }}</p>
                <p v-for="(action, i) in message.otherRulesActions" :key="i" class="text-sm">{{ action.ruleName }}: {{ actionLabel(action) }}</p>
              </div>
            </div>
          </li>
        </ul>
      </template>
    </CardContent>
  </Card>
</template>
