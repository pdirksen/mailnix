<script setup lang="ts">
import { describeCondition } from '@/rules/describe'
import { useRuleEditor } from '@/rules/editorContext'
import type { TraceNode } from '@/rules/model'
import { CheckIcon, XIcon } from '@lucide/vue'
import { useI18n } from 'vue-i18n'

/** Why a rule did or did not match one message: the condition tree with each node's outcome. */
defineProps<{ node: TraceNode }>()

const { t } = useI18n()
const { lists, rules } = useRuleEditor()

function describe(node: TraceNode) {
  if (!node.condition) return ''
  return describeCondition(node.condition, {
    t,
    listName: (id) => lists.value.find((l) => l.id === id)?.name,
    ruleName: (id) => rules.value.find((r) => r.id === id)?.name ?? node.referencedRuleName ?? undefined,
  })
}
</script>

<template>
  <div class="grid gap-1 text-sm">
    <div class="flex items-start gap-2">
      <CheckIcon v-if="node.result" class="text-success mt-0.5 size-4 shrink-0" :aria-label="'✓'" />
      <XIcon v-else class="text-destructive mt-0.5 size-4 shrink-0" :aria-label="'✗'" />
      <div class="min-w-0">
        <span v-if="node.operator" class="font-medium">
          {{ $t('rules.groups.match') }} {{ $t(`rules.groups.${node.operator}`) }} {{ $t('rules.groups.ofTheFollowing') }}
        </span>
        <span v-else>{{ describe(node) }}</span>
        <span v-if="node.note" class="bg-warning/20 ml-2 rounded px-1.5 py-0.5 text-xs">{{ $t(`rules.trace.notes.${node.note}`) }}</span>
        <p v-if="node.condition && !node.referencedRuleName && node.condition.field !== 'Rule'" class="text-muted-foreground text-xs break-all">
          {{ node.actual?.length ? $t('rules.trace.found', { values: node.actual.join(', ') }) : $t('rules.trace.nothingFound') }}
        </p>
      </div>
    </div>
    <div v-if="node.children?.length" class="ml-2 grid gap-1 border-l pl-4">
      <ConditionTrace v-for="(child, index) in node.children" :key="index" :node="child" />
    </div>
  </div>
</template>
