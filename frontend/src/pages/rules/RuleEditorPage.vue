<script setup lang="ts">
import { ApiError } from '@/api/client'
import FormField from '@/components/common/FormField.vue'
import ActionList from '@/components/rules/ActionList.vue'
import ConditionGroupEditor from '@/components/rules/ConditionGroupEditor.vue'
import CronInput from '@/components/rules/CronInput.vue'
import PreviewPanel from '@/components/rules/PreviewPanel.vue'
import SourceFoldersPicker from '@/components/rules/SourceFoldersPicker.vue'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { RadioGroup, RadioGroupItem } from '@/components/ui/radio-group'
import { Skeleton } from '@/components/ui/skeleton'
import { Switch } from '@/components/ui/switch'
import { useConnectionErrors } from '@/lib/connectionErrors'
import { provideRuleEditor } from '@/rules/editorContext'
import { type ConditionField, type FieldDefinition, newCondition, newGroup, type RuleTrigger, toDraft } from '@/rules/model'
import { type Account, useAccountsStore } from '@/stores/accounts'
import { useListsStore } from '@/stores/lists'
import { type RuleDraft, useRulesStore } from '@/stores/rules'
import { ArrowLeftIcon, CircleAlertIcon, Loader2Icon } from '@lucide/vue'
import { useDebounceFn } from '@vueuse/core'
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { onBeforeRouteLeave, RouterLink, useRoute, useRouter } from 'vue-router'
import { toast } from 'vue-sonner'

const props = defineProps<{ id?: string }>()

const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const rulesStore = useRulesStore()
const listsStore = useListsStore()
const accountsStore = useAccountsStore()
const { describeRequestError } = useConnectionErrors()

const TRIGGERS: RuleTrigger[] = ['OnNew', 'Scheduled', 'Manual']

const loading = ref(true)
const notFound = ref(false)
const ruleId = ref<string | undefined>(props.id)
const account = ref<Account | null>(null)
const draft = ref<RuleDraft | null>(null)
const savedSnapshot = ref('')

// Shared with all builder components.
const catalog = ref(new Map<ConditionField, FieldDefinition>())
const folders = ref<string[]>([])
const delimiter = ref('/')
const serverErrors = ref<Record<string, string>>({})
const showErrors = ref(false)
const valid = ref(false)

const nameError = computed(() => (draft.value && !draft.value.name.trim() ? t('validation.required') : undefined))
const visibleErrors = computed(() => (showErrors.value ? serverErrors.value : {}))
const referencableRules = computed(() => rulesStore.rules.filter((r) => r.id !== ruleId.value))
const listsRef = computed(() => listsStore.lists)

provideRuleEditor({ catalog, lists: listsRef, rules: referencableRules, errors: visibleErrors, folders, delimiter })

const dirty = computed(() => draft.value !== null && JSON.stringify(draft.value) !== savedSnapshot.value)

onMounted(async () => {
  try {
    const [fields] = await Promise.all([rulesStore.loadFieldCatalog(), listsStore.fetchAll(), rulesStore.fetchAll(), accountsStore.fetchAll()])
    catalog.value = new Map(fields.map((f) => [f.field, f]))

    let accountId: string | undefined
    if (props.id) {
      const rule = await rulesStore.fetchOne(props.id)
      accountId = rule.accountId
      draft.value = toDraft(rule)
    } else {
      accountId = typeof route.query.account === 'string' ? route.query.account : accountsStore.accounts[0]?.id
      const pending = rulesStore.pendingDraft
      draft.value = pending ?? {
        name: '',
        description: '',
        enabled: true,
        stopProcessing: false,
        dryRun: false,
        trigger: 'OnNew',
        schedule: '',
        sourceFolders: [],
        conditions: newGroup('All', [newCondition(catalog.value.get('From')!)]),
        actions: [],
      }
      rulesStore.pendingDraft = null
      // A rule prefilled from a template or copy is an unsaved change; an untouched blank one is not.
      savedSnapshot.value = pending ? '' : JSON.stringify(draft.value)
    }

    account.value = accountsStore.accounts.find((a) => a.id === accountId) ?? null
    if (!account.value) {
      notFound.value = true
      return
    }
    if (props.id) savedSnapshot.value = JSON.stringify(draft.value)
    void loadFolders(account.value.id)
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) notFound.value = true
    else toast.error(describeRequestError(error).message)
  } finally {
    loading.value = false
  }
})

async function loadFolders(accountId: string) {
  try {
    const list = await accountsStore.folders(accountId, false)
    folders.value = list.folders.filter((f) => f.canSelect).map((f) => f.path)
    delimiter.value = list.delimiter
  } catch {
    // Folder pickers stay empty; typing folder names still works.
  }
}

// Validate against the server as the user edits (rules, lists and references are checked there).
const validate = useDebounceFn(async () => {
  if (!draft.value || !account.value) return
  try {
    const result = await rulesStore.validate(account.value.id, draft.value, ruleId.value)
    const errors: Record<string, string> = {}
    for (const error of result.errors) errors[error.path] ??= error.message
    serverErrors.value = errors
    valid.value = result.valid
  } catch {
    valid.value = false
  }
}, 400)

watch(draft, () => void validate(), { deep: true })
watch(account, () => void validate())

const saving = ref(false)
const saveError = ref<string[]>([])

async function save() {
  if (!draft.value || !account.value) return
  showErrors.value = true
  await validate()
  if (!valid.value || nameError.value) {
    toast.error(t('rules.editor.invalid'))
    return
  }
  saving.value = true
  saveError.value = []
  try {
    const isNew = !ruleId.value
    if (ruleId.value) {
      await rulesStore.update(ruleId.value, draft.value)
    } else {
      ruleId.value = (await rulesStore.create(account.value.id, draft.value)).id
    }
    savedSnapshot.value = JSON.stringify(draft.value)
    // Switch to the rule's own URL before announcing success, so a click right after saving is not overridden.
    if (isNew) await router.replace({ name: 'rule-edit', params: { id: ruleId.value } })
    toast.success(isNew ? t('rules.created') : t('rules.saved'))
  } catch (error) {
    saveError.value = error instanceof ApiError ? error.messages : [describeRequestError(error).message]
  } finally {
    saving.value = false
  }
}

onBeforeRouteLeave((to) => {
  // Saving a new rule moves to its edit URL; that is not leaving.
  if (to.name === 'rule-edit' && to.params.id === ruleId.value) return true
  return !dirty.value || window.confirm(t('rules.editor.unsaved'))
})
</script>

<template>
  <div class="mx-auto grid max-w-[96rem] gap-6">
    <RouterLink
      :to="{ name: 'rules', query: account ? { account: account.id } : {} }"
      class="text-muted-foreground hover:text-foreground flex w-fit items-center gap-1 text-sm"
    >
      <ArrowLeftIcon class="size-4" />
      {{ $t('rules.title') }}
    </RouterLink>

    <p v-if="notFound" class="text-muted-foreground">{{ $t('rules.notFound') }}</p>
    <Skeleton v-else-if="loading || !draft || !account" class="h-96" />

    <template v-else>
      <div class="flex flex-wrap items-center justify-between gap-4">
        <div class="flex flex-wrap items-center gap-3">
          <h2 class="text-2xl font-semibold tracking-tight">{{ ruleId ? $t('rules.editor.editTitle') : $t('rules.editor.newTitle') }}</h2>
          <Badge variant="secondary">{{ account.name }}</Badge>
          <Badge v-if="dirty" variant="outline" class="border-warning text-warning">{{ $t('lists.unsaved') }}</Badge>
        </div>
        <Button :disabled="saving" data-testid="save-rule" @click="save">
          <Loader2Icon v-if="saving" class="animate-spin" />
          {{ $t('rules.editor.save') }}
        </Button>
      </div>

      <Alert v-if="saveError.length" variant="destructive">
        <CircleAlertIcon />
        <AlertTitle>{{ $t('rules.editor.saveFailed') }}</AlertTitle>
        <AlertDescription><p v-for="message in saveError" :key="message">{{ message }}</p></AlertDescription>
      </Alert>

      <div class="grid items-start gap-6 xl:grid-cols-2">
        <div class="grid min-w-0 gap-6">
          <Card>
            <CardHeader><CardTitle>{{ $t('rules.editor.basics') }}</CardTitle></CardHeader>
            <CardContent class="grid gap-4">
              <FormField id="rule-name" v-slot="aria" :label="$t('rules.editor.name')" :error="showErrors ? nameError : undefined">
                <Input id="rule-name" v-model="draft.name" v-bind="aria" autocomplete="off" />
              </FormField>
              <FormField id="rule-description" v-slot="aria" :label="`${$t('rules.editor.description')} (${$t('common.optional')})`">
                <Input id="rule-description" v-model="draft.description" v-bind="aria" autocomplete="off" />
              </FormField>

              <fieldset class="grid gap-2">
                <legend class="mb-2 text-sm font-medium">{{ $t('rules.editor.trigger') }}</legend>
                <RadioGroup v-model="draft.trigger" class="grid gap-3 sm:grid-cols-3">
                  <div v-for="trigger in TRIGGERS" :key="trigger" class="flex items-start gap-2">
                    <RadioGroupItem :id="`trigger-${trigger}`" :value="trigger" class="mt-0.5" />
                    <Label :for="`trigger-${trigger}`" class="grid gap-1 font-normal">
                      <span class="font-medium">{{ $t(`rules.triggers.${trigger}`) }}</span>
                      <span class="text-muted-foreground text-xs">{{ $t(`rules.triggers.${trigger}Hint`) }}</span>
                    </Label>
                  </div>
                </RadioGroup>
              </fieldset>

              <FormField
                v-if="draft.trigger === 'Scheduled'"
                id="rule-schedule"
                :label="$t('rules.editor.schedule')"
                :hint="$t('rules.editor.scheduleHint')"
                :error="visibleErrors.schedule"
              >
                <CronInput v-model="draft.schedule" input-id="rule-schedule" :invalid="!!visibleErrors.schedule" />
              </FormField>

              <FormField id="rule-folders" :label="$t('rules.editor.sourceFolders')" :hint="$t('rules.editor.sourceFoldersHint')">
                <SourceFoldersPicker v-model="draft.sourceFolders" :folders="folders" :watched-folders="account.watchedFolders" input-id="rule-folders" />
              </FormField>

              <div class="grid gap-3 pt-2">
                <div v-for="option in ['enabled', 'dryRun', 'stopProcessing'] as const" :key="option" class="flex items-start gap-3">
                  <Switch :id="`rule-${option}`" v-model="draft[option]" class="mt-0.5" />
                  <Label :for="`rule-${option}`" class="grid gap-1 font-normal">
                    <span class="font-medium">
                      {{ option === 'enabled' ? $t('rules.enabled') : option === 'dryRun' ? $t('rules.dryRun') : $t('rules.editor.stopProcessing') }}
                    </span>
                    <span class="text-muted-foreground text-xs">{{ $t(`rules.editor.${option}Hint`) }}</span>
                  </Label>
                </div>
              </div>
            </CardContent>
          </Card>

          <Card>
            <CardHeader><CardTitle>{{ $t('rules.editor.conditions') }}</CardTitle></CardHeader>
            <CardContent>
              <ConditionGroupEditor v-model="draft.conditions" path="conditions" />
              <p v-if="visibleErrors.conditions" class="text-destructive mt-2 text-sm" role="alert">{{ visibleErrors.conditions }}</p>
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>{{ $t('rules.editor.actions') }}</CardTitle>
              <CardDescription>{{ $t('rules.subtitle') }}</CardDescription>
            </CardHeader>
            <CardContent>
              <ActionList v-model="draft.actions" />
            </CardContent>
          </Card>
        </div>

        <div class="min-w-0 xl:sticky xl:top-20">
          <PreviewPanel
            :account-id="account.id"
            :rule-id="ruleId"
            :draft="draft"
            :valid="valid"
            :folders="folders.length ? folders : account.watchedFolders"
            :default-folder="draft.sourceFolders[0] ?? account.watchedFolders[0] ?? 'INBOX'"
          />
        </div>
      </div>
    </template>
  </div>
</template>
