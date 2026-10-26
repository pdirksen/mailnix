<script setup lang="ts">
import { Badge } from '@/components/ui/badge'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Switch } from '@/components/ui/switch'
import { type AssistantState, folderTemplate, ROOT_PRESETS, staticPath } from '@/rules/assistant'
import { resolveExample } from '@/rules/folderTemplate'
import { computed } from 'vue'

const props = defineProps<{
  /** Server paths of the account's folders; null while unknown. */
  existing: string[] | null
  delimiter: string
  errors: Record<string, string>
}>()
const state = defineModel<AssistantState>({ required: true })

const CUSTOM = '__custom__'
const rootChoice = computed({
  get: () => ((ROOT_PRESETS as readonly string[]).includes(state.value.root) ? state.value.root || '__top__' : CUSTOM),
  set: (value) => {
    if (value === CUSTOM) state.value.root = state.value.root || 'INBOX/'
    else state.value.root = value === '__top__' ? '' : value
  },
})

const existing = computed(() => new Set(props.existing ?? []))

function preview(name: string, suffix: string | null) {
  return resolveExample(folderTemplate(state.value.root, { name, suffix }), props.delimiter)
}

function exists(name: string, suffix: string | null) {
  const path = staticPath(folderTemplate(state.value.root, { name, suffix }), props.delimiter)
  return path !== null && existing.value.has(path)
}
</script>

<template>
  <div class="grid gap-6">
    <div class="grid gap-2 sm:grid-cols-2">
      <div class="grid gap-2">
        <Label for="assistant-root">{{ $t('assistant.folderStep.root') }}</Label>
        <Select v-model="rootChoice">
          <SelectTrigger id="assistant-root" class="w-full">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="INBOX">{{ $t('assistant.folderStep.rootInbox') }}</SelectItem>
            <SelectItem value="__top__">{{ $t('assistant.folderStep.rootTop') }}</SelectItem>
            <SelectItem :value="CUSTOM">{{ $t('assistant.folderStep.rootCustom') }}</SelectItem>
          </SelectContent>
        </Select>
      </div>
      <div v-if="rootChoice === CUSTOM" class="grid gap-2">
        <Label for="assistant-root-custom">{{ $t('assistant.folderStep.rootPath') }}</Label>
        <Input id="assistant-root-custom" v-model="state.root" class="font-mono" spellcheck="false" placeholder="INBOX/Sorted" />
      </div>
    </div>

    <ul class="grid gap-2" data-testid="assistant-folders">
      <li v-for="folder in state.folders" :key="folder.key" class="flex flex-wrap items-center gap-3 rounded-lg border p-3">
        <Switch
          :id="`folder-${folder.key}`"
          v-model="folder.enabled"
          :aria-label="$t('assistant.folderStep.use', { name: folder.name })"
          :data-testid="`folder-toggle-${folder.key}`"
        />
        <div class="grid min-w-0 flex-1 gap-1">
          <div class="flex flex-wrap items-center gap-2">
            <Input
              v-model="folder.name"
              class="h-8 max-w-64"
              :disabled="!folder.enabled"
              :aria-label="$t('assistant.folderStep.name')"
              :aria-invalid="!!errors[`folders.${folder.key}`]"
            />
            <span v-if="folder.suffix" class="text-muted-foreground font-mono text-sm">/{{ folder.suffix }}</span>
            <Badge v-if="folder.enabled && exists(folder.name, folder.suffix)" variant="secondary">{{ $t('assistant.folderStep.exists') }}</Badge>
          </div>
          <p v-if="errors[`folders.${folder.key}`]" class="text-destructive text-sm" role="alert">{{ errors[`folders.${folder.key}`] }}</p>
          <p v-else-if="folder.enabled" class="text-muted-foreground truncate font-mono text-xs">
            {{ preview(folder.name, folder.suffix) ?? folderTemplate(state.root, folder) }}
          </p>
        </div>
      </li>
    </ul>
  </div>
</template>
