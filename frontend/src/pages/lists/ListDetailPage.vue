<script setup lang="ts">
import { ApiError } from '@/api/client'
import FormField from '@/components/common/FormField.vue'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
  AlertDialogTrigger,
} from '@/components/ui/alert-dialog'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { Textarea } from '@/components/ui/textarea'
import { useConnectionErrors } from '@/lib/connectionErrors'
import { mergeEntries, parseEntryLines } from '@/lib/listEntries'
import { type ListEntry, type ListImportResult, type NamedList, useListsStore } from '@/stores/lists'
import { ArrowLeftIcon, CircleAlertIcon, DownloadIcon, Loader2Icon, Trash2Icon, UploadIcon, XIcon } from '@lucide/vue'
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { onBeforeRouteLeave, RouterLink, useRouter } from 'vue-router'
import { toast } from 'vue-sonner'

const props = defineProps<{ id: string }>()

const { t } = useI18n()
const store = useListsStore()
const router = useRouter()
const { describeRequestError } = useConnectionErrors()

const list = ref<NamedList | null>(null)
const notFound = ref(false)
const name = ref('')
const description = ref('')
const entries = ref<ListEntry[]>([])
const dirty = ref(false)

function load(value: NamedList) {
  list.value = value
  name.value = value.name
  description.value = value.description ?? ''
  entries.value = value.entries.map((e) => ({ ...e }))
  dirty.value = false
}

onMounted(async () => {
  try {
    load(await store.fetchOne(props.id))
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) notFound.value = true
    else toast.error(describeRequestError(error).message)
  }
})

onBeforeRouteLeave(() => !dirty.value || window.confirm(t('lists.unsaved')))

// Adding entries by pasting
const pasted = ref('')
function addPasted() {
  const { entries: merged, skipped } = mergeEntries(entries.value, parseEntryLines(pasted.value))
  const added = merged.length - entries.value.length
  entries.value = merged
  pasted.value = ''
  dirty.value ||= added > 0
  toast.info(t('lists.added', { added, skipped }))
}

function removeEntry(value: string) {
  entries.value = entries.value.filter((e) => e.value !== value)
  dirty.value = true
}

const search = ref('')
const visibleEntries = computed(() => {
  const query = search.value.trim().toLowerCase()
  return query ? entries.value.filter((e) => e.value.toLowerCase().includes(query) || e.note?.toLowerCase().includes(query)) : entries.value
})

// Saving
const saving = ref(false)
const saveErrors = ref<string[]>([])
const nameError = ref<string>()

async function save() {
  nameError.value = name.value.trim() ? undefined : t('validation.required')
  if (nameError.value) return
  saving.value = true
  saveErrors.value = []
  try {
    load(await store.update(props.id, { name: name.value.trim(), description: description.value.trim() || null, entries: entries.value }))
    toast.success(t('common.saved'))
  } catch (error) {
    saveErrors.value = error instanceof ApiError ? error.messages : [describeRequestError(error).message]
  } finally {
    saving.value = false
  }
}

// Import / export
const fileInput = ref<HTMLInputElement>()
const replaceOnImport = ref(false)
const importResult = ref<ListImportResult | null>(null)
const importing = ref(false)

async function importFile(event: Event) {
  const file = (event.target as HTMLInputElement).files?.[0]
  if (!file) return
  importing.value = true
  try {
    importResult.value = await store.importCsv(props.id, await file.text(), replaceOnImport.value)
    load(importResult.value.list)
    toast.success(t('lists.imported', { added: importResult.value.added, duplicates: importResult.value.duplicates }))
  } catch (error) {
    toast.error(describeRequestError(error).message)
  } finally {
    importing.value = false
    if (fileInput.value) fileInput.value.value = ''
  }
}

// Deleting
const deleteError = ref<{ title: string; rules: string[] } | null>(null)
async function deleteList() {
  try {
    await store.remove(props.id)
    dirty.value = false
    toast.success(t('lists.deleted'))
    await router.push({ name: 'lists' })
  } catch (error) {
    if (error instanceof ApiError && error.status === 409) {
      const dependents = (error.problem as { dependents?: { name: string }[] }).dependents ?? []
      deleteError.value = { title: t('lists.inUse'), rules: dependents.map((d) => d.name) }
    } else {
      toast.error(describeRequestError(error).message)
    }
  }
}
</script>

<template>
  <div class="mx-auto grid max-w-5xl gap-6">
    <RouterLink :to="{ name: 'lists' }" class="text-muted-foreground hover:text-foreground flex w-fit items-center gap-1 text-sm">
      <ArrowLeftIcon class="size-4" />
      {{ $t('lists.title') }}
    </RouterLink>

    <p v-if="notFound" class="text-muted-foreground">{{ $t('lists.notFound') }}</p>
    <Skeleton v-else-if="!list" class="h-40" />

    <template v-else>
      <div class="flex flex-wrap items-start justify-between gap-4">
        <div class="flex flex-wrap items-center gap-3">
          <h2 class="text-2xl font-semibold tracking-tight">{{ list.name }}</h2>
          <Badge variant="secondary">{{ $t(`lists.kinds.${list.kind}`) }}</Badge>
          <Badge v-if="dirty" variant="outline" class="border-warning text-warning">{{ $t('lists.unsaved') }}</Badge>
        </div>
        <div class="flex flex-wrap gap-2">
          <Button variant="outline" as-child>
            <a :href="store.exportUrl(list.id)" download>
              <DownloadIcon />
              {{ $t('lists.export') }}
            </a>
          </Button>
          <AlertDialog>
            <AlertDialogTrigger as-child>
              <Button variant="outline" class="text-destructive">
                <Trash2Icon />
                {{ $t('common.delete') }}
              </Button>
            </AlertDialogTrigger>
            <AlertDialogContent>
              <AlertDialogHeader>
                <AlertDialogTitle>{{ $t('lists.deleteTitle', { name: list.name }) }}</AlertDialogTitle>
                <AlertDialogDescription>{{ $t('lists.deleteText', { count: list.entries.length }) }}</AlertDialogDescription>
              </AlertDialogHeader>
              <AlertDialogFooter>
                <AlertDialogCancel>{{ $t('common.cancel') }}</AlertDialogCancel>
                <AlertDialogAction class="bg-destructive hover:bg-destructive/90 text-white" @click="deleteList">
                  {{ $t('common.delete') }}
                </AlertDialogAction>
              </AlertDialogFooter>
            </AlertDialogContent>
          </AlertDialog>
        </div>
      </div>

      <Alert v-if="deleteError" variant="destructive" data-testid="list-in-use">
        <CircleAlertIcon />
        <AlertTitle>{{ deleteError.title }}</AlertTitle>
        <AlertDescription>{{ deleteError.rules.join(', ') }}</AlertDescription>
      </Alert>

      <Card>
        <CardContent class="grid gap-4 sm:grid-cols-2">
          <FormField id="list-name" v-slot="aria" :label="$t('common.name')" :error="nameError">
            <Input id="list-name" v-model="name" v-bind="aria" @input="dirty = true" />
          </FormField>
          <FormField id="list-description" v-slot="aria" :label="`${$t('common.description')} (${$t('common.optional')})`">
            <Input id="list-description" v-model="description" v-bind="aria" @input="dirty = true" />
          </FormField>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>{{ $t('lists.addEntries') }}</CardTitle>
          <CardDescription>{{ $t(`lists.kinds.${list.kind}Hint`) }}</CardDescription>
        </CardHeader>
        <CardContent class="grid gap-3">
          <Textarea
            id="list-paste"
            v-model="pasted"
            rows="4"
            :aria-label="$t('lists.addEntries')"
            :placeholder="$t('lists.addEntriesHint')"
            class="font-mono text-sm"
          />
          <div class="flex flex-wrap items-center justify-between gap-3">
            <Button type="button" variant="secondary" :disabled="!pasted.trim()" data-testid="add-entries" @click="addPasted">
              {{ $t('lists.addButton') }}
            </Button>
            <div class="flex flex-wrap items-center gap-3">
              <div class="flex items-center gap-2">
                <Checkbox id="import-replace" v-model="replaceOnImport" />
                <Label for="import-replace" class="text-sm font-normal">{{ $t('lists.importReplace') }}</Label>
              </div>
              <input ref="fileInput" type="file" accept=".csv,.txt,text/csv,text/plain" class="sr-only" data-testid="import-file" @change="importFile" />
              <Button type="button" variant="outline" :disabled="importing" @click="fileInput?.click()">
                <Loader2Icon v-if="importing" class="animate-spin" />
                <UploadIcon v-else />
                {{ $t('lists.import') }}
              </Button>
            </div>
          </div>
          <Alert v-if="importResult && importResult.invalid.length > 0" variant="destructive">
            <CircleAlertIcon />
            <AlertTitle>{{ $t('lists.importInvalid', { count: importResult.invalid.length }, importResult.invalid.length) }}</AlertTitle>
            <AlertDescription>
              <ul class="list-inside list-disc">
                <li v-for="row in importResult.invalid" :key="row.line">
                  {{ $t('lists.importLine', { line: row.line }) }}: <span class="font-mono">{{ row.value }}</span> – {{ row.message }}
                </li>
              </ul>
            </AlertDescription>
          </Alert>
        </CardContent>
      </Card>

      <Card>
        <CardHeader class="flex flex-row flex-wrap items-center justify-between gap-3">
          <CardTitle>{{ $t('lists.entries', { count: entries.length }, entries.length) }}</CardTitle>
          <Input v-model="search" type="search" :placeholder="$t('common.search')" :aria-label="$t('common.search')" class="w-full sm:w-64" />
        </CardHeader>
        <CardContent>
          <p v-if="entries.length === 0" class="text-muted-foreground text-sm">{{ $t('lists.noEntries') }}</p>
          <p v-else-if="visibleEntries.length === 0" class="text-muted-foreground text-sm">{{ $t('lists.noMatches') }}</p>
          <div v-else class="max-h-[32rem] overflow-y-auto rounded-md border">
            <Table data-testid="list-entries">
              <TableHeader>
                <TableRow>
                  <TableHead>{{ $t('lists.entryValue') }}</TableHead>
                  <TableHead>{{ $t('lists.entryNote') }}</TableHead>
                  <TableHead class="w-12" />
                </TableRow>
              </TableHeader>
              <TableBody>
                <TableRow v-for="entry in visibleEntries" :key="entry.value">
                  <TableCell class="font-mono text-sm break-all">{{ entry.value }}</TableCell>
                  <TableCell class="text-muted-foreground">{{ entry.note }}</TableCell>
                  <TableCell>
                    <Button variant="ghost" size="icon" :aria-label="$t('lists.removeEntry', { value: entry.value })" @click="removeEntry(entry.value)">
                      <XIcon />
                    </Button>
                  </TableCell>
                </TableRow>
              </TableBody>
            </Table>
          </div>
        </CardContent>
      </Card>

      <Alert v-if="saveErrors.length > 0" variant="destructive" data-testid="list-save-error">
        <CircleAlertIcon />
        <AlertTitle>{{ $t('lists.saveFailed') }}</AlertTitle>
        <AlertDescription>
          <p v-for="message in saveErrors" :key="message">{{ message }}</p>
        </AlertDescription>
      </Alert>

      <div class="bg-background sticky bottom-0 flex justify-end border-t py-3">
        <Button :disabled="saving || !dirty" data-testid="save-list" @click="save">
          <Loader2Icon v-if="saving" class="animate-spin" />
          {{ $t('common.save') }}
        </Button>
      </div>
    </template>
  </div>
</template>
