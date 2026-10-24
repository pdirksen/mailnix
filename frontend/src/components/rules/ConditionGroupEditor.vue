<script setup lang="ts">
import { Button } from '@/components/ui/button'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { useRuleEditor } from '@/rules/editorContext'
import { childPath, type ConditionGroup, type ConditionNode, type GroupOperator, newCondition, newGroup } from '@/rules/model'
import { ListPlusIcon, PlusIcon, Trash2Icon } from '@lucide/vue'
import ConditionRow from './ConditionRow.vue'

/** Maximum nesting offered in the UI (the server allows a little more). */
const MAX_DEPTH = 4

defineProps<{ path: string; depth?: number; removable?: boolean }>()
const emit = defineEmits<{ remove: [] }>()
const group = defineModel<ConditionGroup>({ required: true })

const { catalog, errors } = useRuleEditor()
const OPERATORS: GroupOperator[] = ['All', 'Any', 'None']

function defaultCondition() {
  return newCondition(catalog.value.get('From') ?? [...catalog.value.values()][0]!)
}

function add(node: ConditionNode) {
  group.value.children.push(node)
}

function remove(index: number) {
  group.value.children.splice(index, 1)
}
</script>

<template>
  <fieldset
    class="grid gap-3 rounded-lg"
    :class="(depth ?? 0) > 0 ? 'bg-muted/40 border p-3' : ''"
    :data-testid="(depth ?? 0) === 0 ? 'condition-root' : 'condition-group'"
  >
    <legend class="sr-only">{{ $t('rules.editor.conditions') }}</legend>
    <div class="flex flex-wrap items-center gap-2 text-sm">
      <span>{{ $t('rules.groups.match') }}</span>
      <Select v-model="group.operator">
        <SelectTrigger class="h-8 w-40" :aria-label="$t('rules.groups.match')">
          <SelectValue />
        </SelectTrigger>
        <SelectContent>
          <SelectItem v-for="op in OPERATORS" :key="op" :value="op">{{ $t(`rules.groups.${op}`) }}</SelectItem>
        </SelectContent>
      </Select>
      <span>{{ $t('rules.groups.ofTheFollowing') }}:</span>
      <Button v-if="removable" variant="ghost" size="sm" class="text-muted-foreground ml-auto" @click="emit('remove')">
        <Trash2Icon />
        {{ $t('rules.groups.removeGroup') }}
      </Button>
    </div>

    <div class="grid gap-3 border-l-2 pl-3">
      <template v-for="(child, index) in group.children" :key="index">
        <ConditionGroupEditor
          v-if="child.kind === 'group'"
          v-model="group.children[index] as ConditionGroup"
          :path="childPath(path, index)"
          :depth="(depth ?? 0) + 1"
          removable
          @remove="remove(index)"
        />
        <ConditionRow v-else v-model="group.children[index] as any" :path="childPath(path, index)" @remove="remove(index)" />
      </template>
      <p v-if="errors[path]" class="text-destructive text-sm" role="alert">{{ errors[path] }}</p>
    </div>

    <div class="flex flex-wrap gap-2">
      <Button variant="outline" size="sm" data-testid="add-condition" @click="add(defaultCondition())">
        <PlusIcon />
        {{ $t('rules.groups.addCondition') }}
      </Button>
      <Button
        v-if="(depth ?? 0) < MAX_DEPTH"
        variant="outline"
        size="sm"
        data-testid="add-group"
        @click="add(newGroup('Any', [defaultCondition()]))"
      >
        <ListPlusIcon />
        {{ $t('rules.groups.addGroup') }}
      </Button>
    </div>
  </fieldset>
</template>
