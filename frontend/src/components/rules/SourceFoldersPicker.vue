<script setup lang="ts">
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Input } from '@/components/ui/input'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { ChevronsUpDownIcon } from '@lucide/vue'
import { computed, ref } from 'vue'

/** Folders a rule runs on; empty means "the account's watched folders". */
const props = defineProps<{ folders: string[]; watchedFolders: string[]; inputId: string }>()
const selected = defineModel<string[]>({ required: true })

const search = ref('')
const filtered = computed(() => {
  const query = search.value.trim().toLowerCase()
  return props.folders.filter((f) => !query || f.toLowerCase().includes(query))
})

function toggle(folder: string, checked: boolean | 'indeterminate') {
  selected.value = checked === true ? [...selected.value, folder] : selected.value.filter((f) => f !== folder)
}
</script>

<template>
  <Popover>
    <PopoverTrigger as-child>
      <Button :id="inputId" variant="outline" class="w-full justify-between font-normal">
        <span class="truncate">
          {{ selected.length ? selected.join(', ') : $t('rules.editor.watchedFolders', { folders: watchedFolders.join(', ') }) }}
        </span>
        <ChevronsUpDownIcon class="opacity-50" />
      </Button>
    </PopoverTrigger>
    <PopoverContent align="start" class="w-80 p-2">
      <Input v-model="search" :placeholder="$t('common.search')" class="mb-2 h-8" />
      <div class="grid max-h-64 gap-1 overflow-y-auto">
        <label v-for="folder in filtered" :key="folder" class="hover:bg-accent flex items-center gap-2 rounded px-2 py-1 text-sm">
          <Checkbox :model-value="selected.includes(folder)" @update:model-value="(c) => toggle(folder, c)" />
          <span class="truncate font-mono">{{ folder }}</span>
        </label>
      </div>
    </PopoverContent>
  </Popover>
</template>
