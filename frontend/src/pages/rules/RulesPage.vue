<script setup lang="ts">
import { ApiError } from '@/api/client'
import RuleTemplatesDialog from '@/components/rules/RuleTemplatesDialog.vue'
import RunNowDialog from '@/components/runs/RunNowDialog.vue'
import UndoRuleDialog from '@/components/runs/UndoRuleDialog.vue'
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
import { Card, CardContent } from '@/components/ui/card'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { Switch } from '@/components/ui/switch'
import { useConnectionErrors } from '@/lib/connectionErrors'
import { formatRelative } from '@/lib/format'
import { countConditions, fromApi, toDraft } from '@/rules/model'
import { useAccountsStore } from '@/stores/accounts'
import { type Rule, useRulesStore } from '@/stores/rules'
import {
  CopyIcon,
  GripVerticalIcon,
  HistoryIcon,
  MoreHorizontalIcon,
  PencilIcon,
  PlayIcon,
  PlusIcon,
  SparklesIcon,
  Trash2Icon,
  Undo2Icon,
  WandSparklesIcon,
} from '@lucide/vue'
import { computed, onMounted, ref, watch } from 'vue'
import { VueDraggable } from 'vue-draggable-plus'
import { useI18n } from 'vue-i18n'
import { RouterLink, useRoute, useRouter } from 'vue-router'
import { toast } from 'vue-sonner'

const { t, locale } = useI18n()
const route = useRoute()
const router = useRouter()
const rules = useRulesStore()
const accounts = useAccountsStore()
const { describeRequestError } = useConnectionErrors()

const loading = ref(true)
const templatesOpen = ref(false)

onMounted(async () => {
  try {
    await Promise.all([accounts.fetchAll(), rules.fetchAll()])
  } catch (error) {
    toast.error(describeRequestError(error).message)
  } finally {
    loading.value = false
  }
})

const accountId = computed({
  get: () => (typeof route.query.account === 'string' ? route.query.account : accounts.accounts[0]?.id),
  set: (id) => void router.replace({ query: { ...route.query, account: id } }),
})

// Local copy for drag & drop; refreshed whenever the store or the account changes.
const ordered = ref<Rule[]>([])
watch(
  () => [rules.rules, accountId.value] as const,
  () => {
    ordered.value = rules.rules.filter((r) => r.accountId === accountId.value).sort((a, b) => a.priority - b.priority)
  },
  { immediate: true, deep: true },
)

async function saveOrder() {
  if (!accountId.value) return
  try {
    await rules.reorder(accountId.value, ordered.value.map((r) => r.id))
    toast.success(t('rules.reordered'))
  } catch (error) {
    toast.error(describeRequestError(error).message)
    await rules.fetchAll()
  }
}

async function setEnabled(rule: Rule, enabled: boolean) {
  try {
    await rules.update(rule.id, { ...toDraft(rule), enabled })
  } catch (error) {
    toast.error(describeRequestError(error).message)
  }
}

function duplicate(rule: Rule) {
  rules.pendingDraft = { ...toDraft(rule), name: t('rules.copyOf', { name: rule.name }) }
  void router.push({ name: 'rule-new', query: { account: rule.accountId } })
}

// The dialog closes before its confirm handler runs, so open state and target are kept separately.
const deleting = ref<Rule | null>(null)
const deleteDialogOpen = ref(false)

function askDelete(rule: Rule) {
  deleting.value = rule
  deleteDialogOpen.value = true
}

async function confirmDelete() {
  const rule = deleting.value
  if (!rule) return
  try {
    await rules.remove(rule.id)
    toast.success(t('rules.deleted'))
  } catch (error) {
    if (error instanceof ApiError && error.status === 409) {
      const dependents = (error.problem as { dependents?: { name: string }[] }).dependents ?? []
      toast.error(t('rules.inUse', { rules: dependents.map((d) => d.name).join(', ') }))
    } else {
      toast.error(describeRequestError(error).message)
    }
  }
}

// "Run now" for all active rules, or "Apply to existing mail" for a single rule.
const runDialogOpen = ref(false)
const runRuleId = ref<string>()

function openRun(rule?: Rule) {
  runRuleId.value = rule?.id
  runDialogOpen.value = true
}

const undoRule = ref<Rule | null>(null)
const undoDialogOpen = ref(false)

function openUndo(rule: Rule) {
  undoRule.value = rule
  undoDialogOpen.value = true
}

function summary(rule: Rule) {
  const conditions = countConditions(fromApi(rule.conditions))
  const actions = rule.actions.length ? rule.actions.map((a) => t(`rules.actions.${a.type}`)).join(', ') : t('rules.noActions')
  return `${t('rules.conditionCount', { count: conditions }, conditions)} → ${actions}`
}
</script>

<template>
  <div class="mx-auto grid max-w-5xl gap-6">
    <div class="flex flex-wrap items-end justify-between gap-4">
      <div class="max-w-2xl">
        <h2 class="text-2xl font-semibold tracking-tight">{{ $t('rules.title') }}</h2>
        <p class="text-muted-foreground mt-1">{{ $t('rules.subtitle') }}</p>
      </div>
      <div v-if="accountId" class="flex flex-wrap gap-2">
        <Button variant="outline" data-testid="run-now" @click="openRun()">
          <PlayIcon />
          {{ $t('runNow.button') }}
        </Button>
        <Button variant="outline" as-child>
          <RouterLink :to="{ name: 'account-setup', params: { id: accountId } }">
            <WandSparklesIcon />
            {{ $t('assistant.open') }}
          </RouterLink>
        </Button>
        <Button variant="outline" data-testid="open-templates" @click="templatesOpen = true">
          <SparklesIcon />
          {{ $t('rules.fromTemplate') }}
        </Button>
        <Button as-child>
          <RouterLink :to="{ name: 'rule-new', query: { account: accountId } }">
            <PlusIcon />
            {{ $t('rules.add') }}
          </RouterLink>
        </Button>
      </div>
    </div>

    <Skeleton v-if="loading" class="h-40" />

    <Card v-else-if="accounts.accounts.length === 0" class="py-10 text-center">
      <CardContent class="grid justify-items-center gap-3">
        <p class="text-muted-foreground">{{ $t('rules.noAccounts') }}</p>
        <Button as-child><RouterLink :to="{ name: 'account-new' }">{{ $t('accounts.add') }}</RouterLink></Button>
      </CardContent>
    </Card>

    <template v-else>
      <div class="flex items-center gap-3">
        <Label for="rules-account">{{ $t('rules.account') }}</Label>
        <Select v-model="accountId">
          <SelectTrigger id="rules-account" class="w-64">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem v-for="account in accounts.accounts" :key="account.id" :value="account.id">{{ account.name }}</SelectItem>
          </SelectContent>
        </Select>
      </div>

      <Card v-if="ordered.length === 0" class="py-10 text-center">
        <CardContent class="grid justify-items-center gap-3">
          <WandSparklesIcon class="text-muted-foreground size-8" />
          <p class="font-medium">{{ $t('rules.empty') }}</p>
          <p class="text-muted-foreground text-sm">{{ $t('rules.emptyHint') }}</p>
          <Button v-if="accountId" as-child class="mt-2" data-testid="empty-assistant">
            <RouterLink :to="{ name: 'account-setup', params: { id: accountId } }">{{ $t('assistant.openEmpty') }}</RouterLink>
          </Button>
        </CardContent>
      </Card>

      <VueDraggable
        v-else
        v-model="ordered"
        tag="ul"
        handle=".drag-handle"
        :animation="150"
        class="grid gap-2"
        data-testid="rule-list"
        @end="saveOrder"
      >
        <li
          v-for="rule in ordered"
          :key="rule.id"
          class="bg-card flex items-center gap-3 rounded-lg border px-3 py-3"
          :class="{ 'opacity-60': !rule.enabled }"
          :data-testid="`rule-${rule.name}`"
        >
          <button type="button" class="drag-handle text-muted-foreground cursor-grab" :aria-label="$t('rules.dragHandle')" :title="$t('rules.dragHandle')">
            <GripVerticalIcon class="size-4" />
          </button>
          <Switch
            :model-value="rule.enabled"
            :aria-label="rule.enabled ? $t('rules.enabled') : $t('rules.disabled')"
            @update:model-value="(value) => setEnabled(rule, value)"
          />
          <div class="min-w-0 flex-1">
            <div class="flex flex-wrap items-center gap-2">
              <RouterLink :to="{ name: 'rule-edit', params: { id: rule.id } }" class="truncate font-medium hover:underline">{{ rule.name }}</RouterLink>
              <Badge v-if="rule.trigger !== 'OnNew'" variant="secondary" :title="rule.schedule ?? undefined">
                {{ $t(`rules.triggers.${rule.trigger}`)
                }}<template v-if="rule.nextRunAt"> · {{ $t('rules.nextRun', { time: formatRelative(rule.nextRunAt, locale) }) }}</template>
              </Badge>
              <Badge v-if="rule.dryRun" variant="outline">{{ $t('rules.dryRun') }}</Badge>
              <Badge v-if="rule.actions.length === 0" variant="outline">{{ $t('rules.filterOnly') }}</Badge>
              <Badge v-if="rule.usedBy.length" variant="outline">{{ $t('rules.usedBy', { count: rule.usedBy.length }, rule.usedBy.length) }}</Badge>
            </div>
            <p class="text-muted-foreground truncate text-sm">{{ summary(rule) }}</p>
          </div>
          <DropdownMenu>
            <DropdownMenuTrigger as-child>
              <Button variant="ghost" size="icon" :aria-label="$t('rules.edit')"><MoreHorizontalIcon /></Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end">
              <DropdownMenuItem @select="router.push({ name: 'rule-edit', params: { id: rule.id } })">
                <PencilIcon /> {{ $t('rules.edit') }}
              </DropdownMenuItem>
              <DropdownMenuItem @select="duplicate(rule)"><CopyIcon /> {{ $t('rules.duplicate') }}</DropdownMenuItem>
              <DropdownMenuSeparator />
              <DropdownMenuItem v-if="rule.actions.length" @select="openRun(rule)"><PlayIcon /> {{ $t('runNow.ruleTitle') }}</DropdownMenuItem>
              <DropdownMenuItem @select="router.push({ name: 'activity', query: { account: rule.accountId } })">
                <HistoryIcon /> {{ $t('nav.activity') }}
              </DropdownMenuItem>
              <DropdownMenuItem @select="openUndo(rule)"><Undo2Icon /> {{ $t('undoRule.menu') }}</DropdownMenuItem>
              <DropdownMenuSeparator />
              <DropdownMenuItem class="text-destructive" @select="askDelete(rule)"><Trash2Icon /> {{ $t('rules.delete') }}</DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
        </li>
      </VueDraggable>
    </template>

    <RuleTemplatesDialog v-if="accountId" v-model:open="templatesOpen" :account-id="accountId" />
    <RunNowDialog v-if="accountId" v-model:open="runDialogOpen" :account-id="accountId" :rule-id="runRuleId" />
    <UndoRuleDialog v-if="undoRule" v-model:open="undoDialogOpen" :rule-id="undoRule.id" :rule-name="undoRule.name" />

    <AlertDialog v-model:open="deleteDialogOpen">
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>{{ $t('rules.deleteTitle', { name: deleting?.name ?? '' }) }}</AlertDialogTitle>
          <AlertDialogDescription>{{ $t('rules.deleteText') }}</AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel>{{ $t('common.cancel') }}</AlertDialogCancel>
          <AlertDialogAction class="bg-destructive hover:bg-destructive/90 text-white" @click="confirmDelete">{{ $t('rules.delete') }}</AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  </div>
</template>
