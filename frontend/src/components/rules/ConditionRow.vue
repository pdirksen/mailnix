<script setup lang="ts">
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import {
  Select,
  SelectContent,
  SelectGroup,
  SelectItem,
  SelectLabel,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { Toggle } from '@/components/ui/toggle'
import { useRuleEditor } from '@/rules/editorContext'
import { CASE_OPERATORS, type Condition, type ConditionField, type ConditionOperator, newCondition, UNARY_OPERATORS } from '@/rules/model'
import { CaseSensitiveIcon, XIcon } from '@lucide/vue'
import { computed } from 'vue'
import ValueInput from './ValueInput.vue'

const props = defineProps<{ path: string }>()
const emit = defineEmits<{ remove: [] }>()
const condition = defineModel<Condition>({ required: true })

const { catalog, errors } = useRuleEditor()

const definition = computed(() => catalog.value.get(condition.value.field))

const CATEGORY_ORDER = ['Sender', 'Recipients', 'Content', 'Status', 'MailType', 'SenderTrust', 'Attachments', 'Rules'] as const
const fieldGroups = computed(() =>
  CATEGORY_ORDER.map((category) => ({
    category,
    fields: [...catalog.value.values()].filter((d) => d.category === category).map((d) => d.field),
  })).filter((g) => g.fields.length > 0),
)

const error = (suffix = '') => errors.value[props.path + suffix]
const id = computed(() => `cond-${props.path.replace(/[^a-z0-9]/gi, '-')}`)

function changeField(field: ConditionField) {
  const next = catalog.value.get(field)
  if (!next) return
  const previous = definition.value
  const fresh = newCondition(next)
  condition.value = {
    ...fresh,
    // Keep what still fits, e.g. "contains 'x'" when switching from Subject to From.
    operator: next.operators.includes(condition.value.operator) ? condition.value.operator : fresh.operator,
    value: previous?.valueType === next.valueType && next.valueType !== 'Enum' ? condition.value.value : fresh.value,
    caseSensitive: condition.value.caseSensitive,
  }
}

function changeOperator(operator: ConditionOperator) {
  const wasList = condition.value.operator === 'InList' || condition.value.operator === 'NotInList'
  const isList = operator === 'InList' || operator === 'NotInList'
  condition.value = { ...condition.value, operator, value: UNARY_OPERATORS.includes(operator) || wasList !== isList ? null : condition.value.value }
}
</script>

<template>
  <div class="flex flex-wrap items-start gap-2" data-testid="condition-row">
    <Select :model-value="condition.field" @update:model-value="(v) => changeField(v as ConditionField)">
      <SelectTrigger :id="`${id}-field`" class="w-56" :aria-label="$t('rules.editor.conditions')">
        <SelectValue />
      </SelectTrigger>
      <SelectContent>
        <SelectGroup v-for="group in fieldGroups" :key="group.category">
          <SelectLabel>{{ $t(`rules.categories.${group.category}`) }}</SelectLabel>
          <SelectItem v-for="field in group.fields" :key="field" :value="field">{{ $t(`rules.fields.${field}`) }}</SelectItem>
        </SelectGroup>
      </SelectContent>
    </Select>

    <Input
      v-if="definition?.requiresHeaderName"
      :id="`${id}-header`"
      :model-value="condition.headerName ?? ''"
      :placeholder="$t('rules.value.headerName')"
      :aria-label="$t('rules.value.headerName')"
      :aria-invalid="!!error('.headerName')"
      class="w-40 font-mono"
      spellcheck="false"
      @update:model-value="(v) => (condition.headerName = String(v))"
    />

    <Select v-if="definition" :model-value="condition.operator" @update:model-value="(v) => changeOperator(v as ConditionOperator)">
      <SelectTrigger :id="`${id}-operator`" class="w-44" :aria-label="$t(`rules.fields.${condition.field}`)">
        <SelectValue />
      </SelectTrigger>
      <SelectContent>
        <SelectItem v-for="op in definition.operators" :key="op" :value="op">{{ $t(`rules.operators.${op}`) }}</SelectItem>
      </SelectContent>
    </Select>

    <!-- Wraps to its own line rather than shrinking below a usable width. -->
    <div v-if="definition && !UNARY_OPERATORS.includes(condition.operator)" class="min-w-48 flex-1 basis-48">
      <ValueInput
        v-model="condition.value"
        :definition="definition"
        :condition="condition"
        :input-id="`${id}-value`"
        :invalid="!!error('.value')"
      />
    </div>
    <div v-else class="flex-1" />

    <Toggle
      v-if="definition && ['Text', 'Address', 'Domain'].includes(definition.valueType) && CASE_OPERATORS.includes(condition.operator)"
      v-model="condition.caseSensitive"
      size="sm"
      :aria-label="$t('rules.value.caseSensitive')"
      :title="$t('rules.value.caseSensitive')"
    >
      <CaseSensitiveIcon />
    </Toggle>

    <Button variant="ghost" size="icon" :aria-label="$t('rules.groups.removeCondition')" @click="emit('remove')">
      <XIcon />
    </Button>

    <p v-for="suffix in ['', '.field', '.operator', '.headerName', '.value']" v-show="error(suffix)" :key="suffix" class="text-destructive w-full text-sm" role="alert">
      {{ error(suffix) }}
    </p>
  </div>
</template>
