<script setup lang="ts">
import FolderStep from '@/components/assistant/FolderStep.vue'
import RuleStep from '@/components/assistant/RuleStep.vue'
import RunNowDialog from '@/components/runs/RunNowDialog.vue'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { Stepper, StepperDescription, StepperIndicator, StepperItem, StepperSeparator, StepperTitle } from '@/components/ui/stepper'
import { Switch } from '@/components/ui/switch'
import { ApiError } from '@/api/client'
import { useConnectionErrors } from '@/lib/connectionErrors'
import { activeLists, activeRules, type AssistantState, type Blueprint, initialState, toSelection } from '@/rules/assistant'
import { type Analysis, type ApplyResult, useAssistantStore } from '@/stores/assistant'
import { type FolderList, useAccountsStore } from '@/stores/accounts'
import { useListsStore } from '@/stores/lists'
import { useRulesStore } from '@/stores/rules'
import { ArrowLeftIcon, CircleAlertIcon, CircleCheckIcon, FolderTreeIcon, Loader2Icon, PlayIcon, RocketIcon } from '@lucide/vue'
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterLink } from 'vue-router'
import { toast } from 'vue-sonner'

const props = defineProps<{ id: string }>()

type Step = 1 | 2 | 3 | 4 | 5

const { t } = useI18n()
const accounts = useAccountsStore()
const assistant = useAssistantStore()
const lists = useListsStore()
const rules = useRulesStore()
const { describeRequestError } = useConnectionErrors()

const account = computed(() => accounts.accounts.find((a) => a.id === props.id))
const loading = ref(true)
const loadError = ref<string | null>(null)
const folders = ref<FolderList | null>(null)

const step = ref<Step>(1)
const state = ref<AssistantState | null>(null)
const blueprint = computed<Blueprint | undefined>(() => assistant.blueprints.find((b) => b.id === state.value?.blueprintId))
const errors = ref<Record<string, string>>({})

const analysis = ref<Analysis | null>(null)
const stale = ref(false)
const analyzing = ref(false)

const dryRun = ref(true)
const createFolders = ref(true)
const runOnInbox = ref(false)
const runLimit = ref(200)
const applying = ref(false)
const applyError = ref<string | null>(null)
const result = ref<ApplyResult | null>(null)
const live = ref(false)
const goingLive = ref(false)
const runOpen = ref(false)

const steps = computed(() =>
  (['blueprint', 'folders', 'rules', 'review'] as const).map((name, i) => ({
    step: i + 1,
    title: t(`assistant.steps.${name}`),
    description: t(`assistant.steps.${name}Description`),
  })),
)

onMounted(async () => {
  try {
    await Promise.all([accounts.fetchOne(props.id), assistant.loadBlueprints(), lists.fetchAll()])
  } catch (error) {
    loadError.value = describeRequestError(error).message
  } finally {
    loading.value = false
  }

  // Only used to mark folders that already exist; the assistant works without it.
  try {
    folders.value = await accounts.folders(props.id, false)
  } catch {
    folders.value = null
  }
})

watch(
  state,
  () => {
    if (analysis.value) stale.value = true
  },
  { deep: true },
)

function choose(choice: Blueprint) {
  // Gmail labels live at the top level; other servers usually keep folders below the inbox.
  state.value = initialState(choice, t, account.value?.provider === 'Google' ? '' : 'INBOX')
  analysis.value = null
  stale.value = false
  errors.value = {}
  step.value = 2
}

function folderName(choice: Blueprint, key: string) {
  const folder = choice.folders.find((f) => f.key === key)!
  return [t(`assistant.folders.${key}`), folder.suffix].filter(Boolean).join('/')
}

const selection = computed(() => (state.value && blueprint.value ? toSelection(state.value, blueprint.value, t) : null))
const selectedRules = computed(() => (state.value && blueprint.value ? activeRules(state.value, blueprint.value) : []))
const reviewRules = computed(() =>
  (selection.value?.rules ?? []).map((rule) => {
    const folder = selectedRules.value.find((r) => r.key === rule.key)?.folder
    return { key: rule.key, name: rule.name, folder: folder ? selection.value!.folders[folder] : undefined }
  }),
)
const newLists = computed(() => (state.value && blueprint.value ? activeLists(state.value, blueprint.value).filter((l) => l.listId === null) : []))

/** Field errors of a validation problem, keyed like "rules.travel.folder"; null for other errors. */
function fieldErrors(error: unknown): Record<string, string> | null {
  if (!(error instanceof ApiError) || !error.problem.errors) return null
  return Object.fromEntries(Object.entries(error.problem.errors).map(([key, messages]) => [key, messages[0] ?? '']))
}

async function analyze() {
  if (!selection.value) return
  analyzing.value = true
  errors.value = {}
  try {
    analysis.value = await assistant.analyze(props.id, selection.value, analysis.value !== null)
    stale.value = false
  } catch (error) {
    const fields = fieldErrors(error)
    if (fields) errors.value = fields
    else toast.error(describeRequestError(error).message)
  } finally {
    analyzing.value = false
  }
}

async function apply() {
  if (!selection.value) return
  applying.value = true
  applyError.value = null
  errors.value = {}
  try {
    result.value = await assistant.apply(props.id, {
      selection: selection.value,
      dryRun: dryRun.value,
      createFolders: createFolders.value,
      runOnInboxLimit: runOnInbox.value ? runLimit.value : null,
    })
    live.value = !dryRun.value
    step.value = 5
    void rules.fetchAll().catch(() => {})
    void lists.fetchAll().catch(() => {})
  } catch (error) {
    const fields = fieldErrors(error)
    if (fields) {
      errors.value = fields
      applyError.value = t('assistant.review.fixErrors')
    } else {
      applyError.value = describeRequestError(error).message
    }
  } finally {
    applying.value = false
  }
}

async function goLive() {
  if (!result.value) return
  goingLive.value = true
  try {
    await rules.setDryRun(
      result.value.rules.map((r) => r.id),
      false,
    )
    live.value = true
    toast.success(t('assistant.done.liveToast'))
  } catch (error) {
    toast.error(describeRequestError(error).message)
  } finally {
    goingLive.value = false
  }
}
</script>

<template>
  <div class="mx-auto grid max-w-4xl gap-6">
    <RouterLink :to="{ name: 'account-detail', params: { id } }" class="text-muted-foreground hover:text-foreground flex w-fit items-center gap-1 text-sm">
      <ArrowLeftIcon class="size-4" />
      {{ account?.name ?? $t('accounts.title') }}
    </RouterLink>

    <div>
      <h2 class="text-2xl font-semibold tracking-tight">{{ $t('assistant.title') }}</h2>
      <p class="text-muted-foreground mt-1">{{ $t('assistant.subtitle') }}</p>
    </div>

    <Skeleton v-if="loading" class="h-40" />
    <p v-else-if="loadError || !account" class="text-muted-foreground">{{ loadError ?? $t('accounts.notFound') }}</p>

    <template v-else>
      <Stepper v-if="step < 5" :model-value="step" class="flex w-full items-start gap-2" :linear="true">
        <StepperItem
          v-for="item in steps"
          :key="item.step"
          :step="item.step"
          class="relative flex w-full flex-col items-center justify-center"
          :disabled="item.step > step"
        >
          <StepperSeparator
            v-if="item.step !== steps.length"
            class="bg-muted group-data-[state=completed]:bg-primary absolute top-4 right-[calc(-50%+20px)] left-[calc(50%+20px)] block h-0.5 shrink-0 rounded-full"
          />
          <StepperIndicator class="size-8">
            <CircleCheckIcon v-if="item.step < step" class="size-4" />
            <span v-else>{{ item.step }}</span>
          </StepperIndicator>
          <div class="mt-2 flex flex-col items-center text-center">
            <StepperTitle class="text-xs font-medium sm:text-sm">{{ item.title }}</StepperTitle>
            <StepperDescription class="hidden text-xs sm:block">{{ item.description }}</StepperDescription>
          </div>
        </StepperItem>
      </Stepper>

      <!-- Step 1: blueprint -->
      <div v-if="step === 1" class="grid gap-3 sm:grid-cols-2">
        <button
          v-for="choice in assistant.blueprints"
          :key="choice.id"
          type="button"
          class="hover:bg-accent/50 focus-visible:ring-ring/50 grid content-start gap-2 rounded-lg border p-4 text-left outline-none focus-visible:ring-3"
          :data-testid="`blueprint-${choice.id}`"
          @click="choose(choice)"
        >
          <span class="font-medium">{{ $t(`assistant.blueprints.${choice.id}.name`) }}</span>
          <span class="text-muted-foreground text-sm">{{ $t(`assistant.blueprints.${choice.id}.description`) }}</span>
          <ul class="text-muted-foreground mt-1 grid gap-0.5 font-mono text-xs">
            <li v-for="folder in choice.folders.filter((f) => f.defaultOn)" :key="folder.key" class="flex items-center gap-1.5">
              <FolderTreeIcon class="size-3.5 shrink-0" />
              {{ folderName(choice, folder.key) }}
            </li>
          </ul>
        </button>
      </div>

      <!-- Step 2: folders -->
      <Card v-else-if="step === 2 && state">
        <CardHeader>
          <CardTitle>{{ $t('assistant.steps.folders') }}</CardTitle>
          <CardDescription>{{ $t('assistant.folderStep.hint') }}</CardDescription>
        </CardHeader>
        <CardContent>
          <FolderStep
            v-model="state"
            :existing="folders?.folders.map((f) => f.path) ?? null"
            :delimiter="folders?.delimiter ?? '/'"
            :errors="errors"
          />
        </CardContent>
        <CardFooter class="mt-6 flex justify-between">
          <Button type="button" variant="outline" @click="step = 1">{{ $t('common.back') }}</Button>
          <Button type="button" :disabled="!state.folders.some((f) => f.enabled)" data-testid="assistant-next" @click="step = 3">
            {{ $t('common.next') }}
          </Button>
        </CardFooter>
      </Card>

      <!-- Step 3: rules -->
      <Card v-else-if="step === 3 && state && blueprint">
        <CardHeader>
          <CardTitle>{{ $t('assistant.steps.rules') }}</CardTitle>
          <CardDescription>{{ $t('assistant.ruleStep.hint') }}</CardDescription>
        </CardHeader>
        <CardContent>
          <RuleStep v-model="state" :blueprint="blueprint" :analysis="analysis" :stale="stale" :analyzing="analyzing" :errors="errors" @analyze="analyze" />
        </CardContent>
        <CardFooter class="mt-6 flex justify-between">
          <Button type="button" variant="outline" @click="step = 2">{{ $t('common.back') }}</Button>
          <Button type="button" :disabled="selectedRules.length === 0" data-testid="assistant-next" @click="step = 4">{{ $t('common.next') }}</Button>
        </CardFooter>
      </Card>

      <!-- Step 4: review and apply -->
      <Card v-else-if="step === 4 && state && selection">
        <CardHeader>
          <CardTitle>{{ $t('assistant.steps.review') }}</CardTitle>
          <CardDescription>
            {{
              $t('assistant.review.summary', {
                folders: Object.keys(selection.folders).length,
                rules: selection.rules.length,
                lists: newLists.length,
              })
            }}
          </CardDescription>
        </CardHeader>
        <CardContent class="grid gap-5">
          <ul class="grid gap-1 text-sm">
            <li v-for="rule in reviewRules" :key="rule.key" class="flex flex-wrap gap-x-2">
              <span class="font-medium">{{ rule.name }}</span>
              <span v-if="rule.folder" class="text-muted-foreground font-mono">→ {{ rule.folder }}</span>
            </li>
          </ul>

          <div class="flex items-start gap-3">
            <Switch id="assistant-dry-run" v-model="dryRun" class="mt-0.5" data-testid="assistant-dry-run" />
            <Label for="assistant-dry-run" class="grid gap-1 font-normal">
              <span class="font-medium">{{ $t('assistant.review.dryRun') }}</span>
              <span class="text-muted-foreground text-xs">{{ $t('assistant.review.dryRunHint') }}</span>
            </Label>
          </div>
          <div class="flex items-start gap-3">
            <Switch id="assistant-create-folders" v-model="createFolders" class="mt-0.5" />
            <Label for="assistant-create-folders" class="grid gap-1 font-normal">
              <span class="font-medium">{{ $t('assistant.review.createFolders') }}</span>
              <span class="text-muted-foreground text-xs">{{ $t('assistant.review.createFoldersHint') }}</span>
            </Label>
          </div>
          <div class="flex items-start gap-3">
            <Switch id="assistant-run" v-model="runOnInbox" class="mt-0.5" :disabled="!account.enabled" data-testid="assistant-run" />
            <div class="grid gap-2">
              <Label for="assistant-run" class="grid gap-1 font-normal">
                <span class="font-medium">{{ $t('assistant.review.runOnInbox') }}</span>
                <span class="text-muted-foreground text-xs">
                  {{ account.enabled ? $t(dryRun ? 'assistant.review.runOnInboxDryHint' : 'assistant.review.runOnInboxHint') : $t('assistant.review.paused') }}
                </span>
              </Label>
              <div v-if="runOnInbox" class="flex items-center gap-2">
                <Label for="assistant-run-limit" class="font-normal">{{ $t('assistant.review.runLimit') }}</Label>
                <Input id="assistant-run-limit" v-model.number="runLimit" type="number" min="1" max="1000" class="w-28" />
              </div>
            </div>
          </div>

          <Alert v-if="!dryRun" variant="destructive">
            <CircleAlertIcon />
            <AlertTitle>{{ $t('assistant.review.liveWarningTitle') }}</AlertTitle>
            <AlertDescription>{{ $t('assistant.review.liveWarning') }}</AlertDescription>
          </Alert>
          <p v-if="applyError" class="text-destructive text-sm" role="alert">{{ applyError }}</p>
          <ul v-if="Object.keys(errors).length" class="text-destructive list-disc pl-5 text-sm">
            <li v-for="(message, path) in errors" :key="path">
              <span class="font-mono">{{ path }}</span>: {{ message }}
            </li>
          </ul>
        </CardContent>
        <CardFooter class="mt-6 flex justify-between">
          <Button type="button" variant="outline" :disabled="applying" @click="step = 3">{{ $t('common.back') }}</Button>
          <Button type="button" :disabled="applying || (runOnInbox && !(runLimit >= 1 && runLimit <= 1000))" data-testid="assistant-apply" @click="apply">
            <Loader2Icon v-if="applying" class="animate-spin" />
            {{ $t('assistant.review.apply') }}
          </Button>
        </CardFooter>
      </Card>

      <!-- Step 5: done -->
      <Card v-else-if="step === 5 && result" data-testid="assistant-done">
        <CardHeader>
          <CardTitle class="flex items-center gap-2">
            <CircleCheckIcon class="text-success size-5" />
            {{ $t('assistant.done.title', { count: result.rules.length }, result.rules.length) }}
          </CardTitle>
          <CardDescription>{{ live ? $t('assistant.done.liveHint') : $t('assistant.done.dryRunHint') }}</CardDescription>
        </CardHeader>
        <CardContent class="grid gap-4">
          <ul class="grid gap-1 text-sm">
            <li v-for="rule in result.rules" :key="rule.id">
              <RouterLink :to="{ name: 'rule-edit', params: { id: rule.id } }" class="hover:underline">{{ rule.name }}</RouterLink>
            </li>
          </ul>
          <p v-if="result.createdListIds.length" class="text-muted-foreground text-sm">
            {{ $t('assistant.done.lists', { count: result.createdListIds.length }, result.createdListIds.length) }}
          </p>
          <ul v-if="result.folders.some((f) => !f.created)" class="text-destructive grid gap-1 text-sm">
            <li v-for="folder in result.folders.filter((f) => !f.created)" :key="folder.path">
              {{ $t('assistant.done.folderFailed', { folder: folder.path, error: folder.error }) }}
            </li>
          </ul>
          <p v-else-if="result.folders.length" class="text-muted-foreground text-sm">
            {{ $t('assistant.done.folders', { count: result.folders.length }, result.folders.length) }}
          </p>
          <p v-if="result.run" class="text-sm">
            {{ $t('assistant.done.run') }}
            <RouterLink :to="{ name: 'activity', query: { account: id } }" class="font-medium hover:underline">{{ $t('nav.activity') }}</RouterLink>
          </p>
        </CardContent>
        <CardFooter class="mt-6 flex flex-wrap justify-end gap-2">
          <Button variant="outline" as-child>
            <RouterLink :to="{ name: 'rules', query: { account: id } }">{{ $t('assistant.done.toRules') }}</RouterLink>
          </Button>
          <Button v-if="!live" :disabled="goingLive" data-testid="assistant-go-live" @click="goLive">
            <Loader2Icon v-if="goingLive" class="animate-spin" />
            <RocketIcon v-else />
            {{ $t('assistant.done.goLive') }}
          </Button>
          <Button v-else-if="account.enabled" data-testid="assistant-sort-now" @click="runOpen = true">
            <PlayIcon />
            {{ $t('assistant.done.sortNow') }}
          </Button>
        </CardFooter>
      </Card>
    </template>

    <RunNowDialog v-if="account" v-model:open="runOpen" :account-id="id" />
  </div>
</template>
