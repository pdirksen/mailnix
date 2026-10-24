<script setup lang="ts">
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { useRuleEditor } from '@/rules/editorContext'
import { FOLDER_PLACEHOLDERS, pathToTemplate, resolveExample } from '@/rules/folderTemplate'
import { FolderTreeIcon } from '@lucide/vue'
import { computed, ref } from 'vue'

const props = defineProps<{ inputId: string; invalid?: boolean }>()
const template = defineModel<string>({ required: true })

const { folders, delimiter } = useRuleEditor()
const pickerOpen = ref(false)
const search = ref('')

const filtered = computed(() => {
  const query = search.value.trim().toLowerCase()
  return folders.value.filter((f) => !query || f.toLowerCase().includes(query)).slice(0, 200)
})

const example = computed(() => (template.value.trim() ? resolveExample(template.value, delimiter.value) : null))

function pick(path: string) {
  template.value = pathToTemplate(path, delimiter.value)
  pickerOpen.value = false
}

function insert(placeholder: string) {
  const current = template.value.replace(/\/+$/, '')
  template.value = current ? `${current}/{${placeholder}}` : `{${placeholder}}`
  document.getElementById(props.inputId)?.focus()
}
</script>

<template>
  <div class="grid gap-2">
    <div class="flex gap-2">
      <Input
        :id="inputId"
        v-model="template"
        class="flex-1 font-mono text-sm"
        placeholder="INBOX/Archive/{year}"
        spellcheck="false"
        autocomplete="off"
        :aria-invalid="invalid"
      />
      <Popover v-model:open="pickerOpen">
        <PopoverTrigger as-child>
          <Button variant="outline" size="icon" :aria-label="$t('rules.actions.pickFolder')" :title="$t('rules.actions.pickFolder')">
            <FolderTreeIcon />
          </Button>
        </PopoverTrigger>
        <PopoverContent align="end" class="w-80 p-2">
          <Input v-model="search" :placeholder="$t('common.search')" class="mb-2 h-8" />
          <div class="max-h-64 overflow-y-auto" role="listbox">
            <button
              v-for="folder in filtered"
              :key="folder"
              type="button"
              role="option"
              class="hover:bg-accent w-full truncate rounded px-2 py-1 text-left font-mono text-sm"
              @click="pick(folder)"
            >
              {{ folder }}
            </button>
          </div>
        </PopoverContent>
      </Popover>
    </div>
    <div class="flex flex-wrap items-center gap-1">
      <span class="text-muted-foreground mr-1 text-xs">{{ $t('rules.actions.placeholders') }}:</span>
      <button
        v-for="name in FOLDER_PLACEHOLDERS"
        :key="name"
        type="button"
        class="bg-muted hover:bg-accent rounded px-1.5 py-0.5 font-mono text-xs"
        @click="insert(name)"
      >
        {{ '{' + name + '}' }}
      </button>
    </div>
    <p v-if="example" class="text-muted-foreground text-xs" data-testid="folder-example">
      {{ $t('rules.actions.example', { path: example }) }}
    </p>
  </div>
</template>
