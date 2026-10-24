<script setup lang="ts">
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import type { FolderInfo, SpecialFolders } from '@/stores/accounts'
import { computed } from 'vue'
import { SPECIAL_FOLDER_ICONS } from './folderIcons'

const props = defineProps<{ folders: readonly FolderInfo[] }>()
const specialFolders = defineModel<SpecialFolders>({ required: true })

// Reka's Select cannot use an empty string as a value.
const NONE = '__none__'

const KINDS = [
  { key: 'trash', kind: 'Trash' },
  { key: 'junk', kind: 'Junk' },
  { key: 'archive', kind: 'Archive' },
  { key: 'sent', kind: 'Sent' },
  { key: 'drafts', kind: 'Drafts' },
] as const satisfies readonly { key: keyof SpecialFolders; kind: string }[]

const options = computed(() =>
  props.folders.filter((f) => f.canSelect).map((f) => f.path).sort((a, b) => a.localeCompare(b)),
)

function update(key: keyof SpecialFolders, value: unknown) {
  specialFolders.value = { ...specialFolders.value, [key]: value === NONE ? null : String(value) }
}
</script>

<template>
  <div class="grid gap-4 sm:grid-cols-2">
    <div v-for="{ key, kind } in KINDS" :key="key" class="grid gap-2">
      <Label :for="`special-${key}`" class="flex items-center gap-2">
        <component :is="SPECIAL_FOLDER_ICONS[kind]" class="text-muted-foreground size-4" aria-hidden="true" />
        {{ $t(`accounts.folders.special.${kind}`) }}
      </Label>
      <Select :model-value="specialFolders[key] ?? NONE" @update:model-value="(value) => update(key, value)">
        <SelectTrigger :id="`special-${key}`" class="w-full">
          <SelectValue />
        </SelectTrigger>
        <SelectContent>
          <SelectItem :value="NONE">{{ $t('accounts.folders.notSet') }}</SelectItem>
          <SelectItem v-for="path in options" :key="path" :value="path">{{ path }}</SelectItem>
        </SelectContent>
      </Select>
    </div>
  </div>
</template>
