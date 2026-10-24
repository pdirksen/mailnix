<script setup lang="ts">
import { Button } from '@/components/ui/button'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuGroup,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { Input } from '@/components/ui/input'
import { useRuleEditor } from '@/rules/editorContext'
import { type ActionType, isTerminal, newAction, NON_TERMINAL_ACTIONS, type RuleAction, TERMINAL_ACTIONS } from '@/rules/model'
import { ArrowRightIcon, PlusIcon, XIcon } from '@lucide/vue'
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import FolderTemplateInput from './FolderTemplateInput.vue'

const actions = defineModel<RuleAction[]>({ required: true })
const { errors, labels } = useRuleEditor()
const { t, te } = useI18n()

/** Gmail calls folders labels: "Move to label", "Add label". */
function actionLabel(type: ActionType) {
  return labels?.value && te(`rules.gmailActions.${type}`) ? t(`rules.gmailActions.${type}`) : t(`rules.actions.${type}`)
}

const hasTerminal = computed(() => actions.value.some(isTerminal))

// Moving actions are listed last, matching the order in which they run.
const ordered = computed(() =>
  actions.value.map((action, index) => ({ action, index })).sort((a, b) => Number(isTerminal(a.action)) - Number(isTerminal(b.action))),
)

function add(type: ActionType) {
  actions.value.push(newAction(type))
}

function remove(index: number) {
  actions.value.splice(index, 1)
}
</script>

<template>
  <div class="grid gap-3" data-testid="action-list">
    <div
      v-for="{ action, index } in ordered"
      :key="index"
      class="grid gap-2 rounded-lg border p-3"
      :class="{ 'border-primary/40': isTerminal(action) }"
      data-testid="action-row"
    >
      <div class="flex items-center gap-2">
        <ArrowRightIcon v-if="isTerminal(action)" class="text-primary size-4" aria-hidden="true" />
        <span class="font-medium">{{ actionLabel(action.type) }}</span>
        <Button variant="ghost" size="icon" class="ml-auto" :aria-label="$t('rules.actions.remove')" @click="remove(index)">
          <XIcon />
        </Button>
      </div>
      <FolderTemplateInput
        v-if="'folder' in action"
        v-model="action.folder"
        :input-id="`action-${index}-folder`"
        :invalid="!!errors[`actions[${index}].folder`]"
      />
      <Input
        v-if="'keyword' in action"
        v-model="action.keyword"
        :aria-label="$t('rules.actions.keyword')"
        :placeholder="$t('rules.actions.keyword')"
        class="font-mono"
        :aria-invalid="!!errors[`actions[${index}].keyword`]"
      />
      <p v-for="key in [`actions[${index}].folder`, `actions[${index}].keyword`]" v-show="errors[key]" :key="key" class="text-destructive text-sm" role="alert">
        {{ errors[key] }}
      </p>
    </div>

    <p v-if="errors.actions" class="text-destructive text-sm" role="alert">{{ errors.actions }}</p>
    <p v-if="actions.length === 0" class="text-muted-foreground text-sm">{{ $t('rules.editor.actionsHint') }}</p>

    <DropdownMenu>
      <DropdownMenuTrigger as-child>
        <Button variant="outline" size="sm" class="w-fit" data-testid="add-action">
          <PlusIcon />
          {{ $t('rules.actions.add') }}
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start" class="w-64">
        <DropdownMenuLabel>{{ $t('rules.actions.otherGroup') }}</DropdownMenuLabel>
        <DropdownMenuGroup>
          <DropdownMenuItem v-for="type in NON_TERMINAL_ACTIONS" :key="type" @select="add(type)">{{ actionLabel(type) }}</DropdownMenuItem>
        </DropdownMenuGroup>
        <DropdownMenuSeparator />
        <DropdownMenuLabel>{{ $t('rules.actions.terminalGroup') }}</DropdownMenuLabel>
        <DropdownMenuGroup>
          <DropdownMenuItem
            v-for="type in TERMINAL_ACTIONS"
            :key="type"
            :disabled="hasTerminal"
            :title="hasTerminal ? $t('rules.actions.oneTerminal') : undefined"
            @select="add(type)"
          >
            {{ actionLabel(type) }}
          </DropdownMenuItem>
        </DropdownMenuGroup>
      </DropdownMenuContent>
    </DropdownMenu>
  </div>
</template>
