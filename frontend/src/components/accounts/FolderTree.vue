<script setup lang="ts">
import { Badge } from '@/components/ui/badge'
import { Checkbox } from '@/components/ui/checkbox'
import { formatNumber } from '@/lib/format'
import type { FolderInfo } from '@/stores/accounts'
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { folderIcon } from './folderIcons'
import { toFolderRows } from './folderRows'

const props = defineProps<{
  folders: readonly FolderInfo[]
  /** Show checkboxes; selected folder paths are bound with v-model. */
  selectable?: boolean
}>()

const selected = defineModel<string[]>({ default: () => [] })
const { locale, t } = useI18n()

/** The special-use badge, unless it would only repeat the folder name (e.g. "Archive" / "Archive"). */
function showBadge(folder: FolderInfo) {
  if (!folder.specialUse) return false
  return folder.name.localeCompare(t(`accounts.folders.special.${folder.specialUse}`), undefined, { sensitivity: 'base' }) !== 0
}

const rows = computed(() => toFolderRows(props.folders))

function toggle(path: string, checked: boolean | 'indeterminate') {
  selected.value = checked === true ? [...selected.value, path] : selected.value.filter((p) => p !== path)
}
</script>

<template>
  <ul class="divide-y rounded-md border" role="list" data-testid="folder-tree">
    <li
      v-for="{ folder, depth } in rows"
      :key="folder.path"
      class="flex min-h-10 items-center gap-2 px-3 py-1.5 text-sm"
      :style="{ paddingLeft: `${0.75 + depth * 1.25}rem` }"
    >
      <Checkbox
        v-if="selectable"
        :id="`folder-${folder.path}`"
        :model-value="selected.includes(folder.path)"
        :disabled="!folder.canSelect"
        :aria-label="folder.path"
        @update:model-value="(checked) => toggle(folder.path, checked)"
      />
      <component :is="folderIcon(folder.specialUse)" class="text-muted-foreground size-4 shrink-0" aria-hidden="true" />
      <label
        :for="selectable ? `folder-${folder.path}` : undefined"
        class="min-w-0 truncate"
        :class="{ 'text-muted-foreground': !folder.canSelect }"
        :title="folder.path"
      >
        {{ folder.name }}
      </label>
      <Badge v-if="showBadge(folder)" variant="secondary" class="shrink-0">
        {{ $t(`accounts.folders.special.${folder.specialUse}`) }}
      </Badge>
      <span v-if="!folder.canSelect" class="text-muted-foreground shrink-0 text-xs">
        {{ $t('accounts.folders.notSelectable') }}
      </span>
      <span v-if="folder.messages != null" class="text-muted-foreground ml-auto shrink-0 text-xs tabular-nums">
        {{ $t('accounts.folders.messages', { count: formatNumber(folder.messages, locale) }, folder.messages) }}
        <template v-if="folder.unread">
          · <span class="text-foreground font-medium">{{ $t('accounts.folders.unread', { count: formatNumber(folder.unread, locale) }) }}</span>
        </template>
      </span>
    </li>
  </ul>
</template>
