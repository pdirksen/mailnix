<script setup lang="ts">
import { Input } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { splitBytes, UNIT_SIZES } from '@/rules/describe'
import { useRuleEditor } from '@/rules/editorContext'
import type { Condition, FieldDefinition } from '@/rules/model'
import { computed, ref, watch } from 'vue'

/** The operand of a condition, rendered to fit the field's value type and operator. */
const props = defineProps<{ definition: FieldDefinition; condition: Condition; invalid?: boolean; inputId: string }>()
const value = defineModel<string | null | undefined>({ required: true })

const { lists, rules } = useRuleEditor()

const mode = computed(() => {
  const { valueType, unit } = props.definition
  if (props.condition.operator === 'InList' || props.condition.operator === 'NotInList') return 'list'
  if (valueType === 'RuleReference') return 'rule'
  if (valueType === 'Enum') return 'enum'
  if (valueType === 'Date') return 'date'
  if (valueType === 'Number' && unit === 'Bytes') return 'bytes'
  if (valueType === 'Number') return 'number'
  return 'text'
})

// Domain fields cannot use address lists.
const compatibleLists = computed(() =>
  lists.value.filter((l) => props.definition.valueType !== 'Domain' || l.kind !== 'Address'),
)

const PLACEHOLDERS: Partial<Record<string, string>> = {
  From: 'news@shop.example',
  ReplyTo: 'reply@shop.example',
  To: 'me@example.com',
  Cc: 'team@example.com',
  Recipients: 'me@example.com',
  DeliveredTo: 'me+shop@example.com',
  FromDomain: 'shop.example',
  AttachmentExtension: 'pdf',
  AttachmentMimeType: 'application/pdf',
  Keyword: 'invoice',
}

// Byte fields: edit as number + unit, store bytes.
const unit = ref<'B' | 'KB' | 'MB'>('MB')
const amount = ref<number | ''>('')
watch(
  () => [value.value, mode.value] as const,
  ([raw, current]) => {
    if (current !== 'bytes' || raw == null || raw === '' || Number.isNaN(Number(raw))) return
    const split = splitBytes(Number(raw))
    if (Number(amount.value) * UNIT_SIZES[unit.value] !== Number(raw)) {
      amount.value = split.value
      unit.value = split.unit
    }
  },
  { immediate: true },
)

function updateBytes() {
  value.value = amount.value === '' ? '' : String(Math.round(Number(amount.value) * UNIT_SIZES[unit.value]))
}
</script>

<template>
  <Select v-if="mode === 'list'" :model-value="value ?? undefined" @update:model-value="(v) => (value = String(v))">
    <SelectTrigger :id="inputId" class="w-full" :aria-invalid="invalid">
      <SelectValue :placeholder="compatibleLists.length ? $t('rules.value.chooseList') : $t('rules.value.noLists')" />
    </SelectTrigger>
    <SelectContent>
      <SelectItem v-for="list in compatibleLists" :key="list.id" :value="list.id">
        {{ list.name }} ({{ $t(`lists.kinds.${list.kind}`) }})
      </SelectItem>
    </SelectContent>
  </Select>

  <Select v-else-if="mode === 'rule'" :model-value="value ?? undefined" @update:model-value="(v) => (value = String(v))">
    <SelectTrigger :id="inputId" class="w-full" :aria-invalid="invalid">
      <SelectValue :placeholder="rules.length ? $t('rules.value.chooseRule') : $t('rules.value.noRules')" />
    </SelectTrigger>
    <SelectContent>
      <SelectItem v-for="rule in rules" :key="rule.id" :value="rule.id">{{ rule.name }}</SelectItem>
    </SelectContent>
  </Select>

  <Select v-else-if="mode === 'enum'" :model-value="value ?? undefined" @update:model-value="(v) => (value = String(v))">
    <SelectTrigger :id="inputId" class="w-full min-w-32" :aria-invalid="invalid">
      <SelectValue />
    </SelectTrigger>
    <SelectContent>
      <SelectItem v-for="option in definition.options ?? []" :key="option" :value="option">{{ option }}</SelectItem>
    </SelectContent>
  </Select>

  <Input
    v-else-if="mode === 'date'"
    :id="inputId"
    :model-value="value ?? ''"
    type="date"
    class="w-44"
    :aria-invalid="invalid"
    @update:model-value="(v) => (value = String(v))"
  />

  <div v-else-if="mode === 'bytes'" class="flex gap-2">
    <Input :id="inputId" v-model.number="amount" type="number" min="0" step="any" class="w-28" :aria-invalid="invalid" @update:model-value="updateBytes" />
    <Select v-model="unit" @update:model-value="updateBytes">
      <SelectTrigger class="w-24" :aria-label="$t('rules.value.units.MB')">
        <SelectValue />
      </SelectTrigger>
      <SelectContent>
        <SelectItem v-for="u in ['KB', 'MB']" :key="u" :value="u">{{ $t(`rules.value.units.${u}`) }}</SelectItem>
      </SelectContent>
    </Select>
  </div>

  <div v-else-if="mode === 'number'" class="flex items-center gap-2">
    <Input
      :id="inputId"
      :model-value="value ?? ''"
      type="number"
      step="any"
      class="w-28"
      :aria-invalid="invalid"
      @update:model-value="(v) => (value = String(v))"
    />
    <span v-if="definition.unit === 'Days'" class="text-muted-foreground text-sm">{{ $t('rules.value.days') }}</span>
  </div>

  <Input
    v-else
    :id="inputId"
    :model-value="value ?? ''"
    :placeholder="PLACEHOLDERS[definition.field] ?? $t('rules.value.placeholder')"
    :class="{ 'font-mono': condition.operator === 'Regex' }"
    class="w-full"
    autocomplete="off"
    spellcheck="false"
    :aria-invalid="invalid"
    @update:model-value="(v) => (value = String(v))"
  />
</template>
