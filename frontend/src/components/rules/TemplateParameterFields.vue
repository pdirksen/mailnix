<script setup lang="ts">
import FormField from '@/components/common/FormField.vue'
import { Input } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { useListsStore } from '@/stores/lists'
import type { RuleTemplateInfo } from '@/stores/rules'

/** Inputs for a rule template's parameters; lists come from the lists store (loaded by the caller). */
defineProps<{ parameters: RuleTemplateInfo['parameters']; errors?: Record<string, string>; idPrefix: string }>()
const values = defineModel<Record<string, string>>({ required: true })

const lists = useListsStore()

function choices(type: string) {
  return lists.lists.filter((l) => (type === 'AddressList' ? l.kind === 'Address' : l.kind !== 'Address'))
}
</script>

<template>
  <FormField
    v-for="parameter in parameters"
    :id="`${idPrefix}-${parameter.name}`"
    :key="parameter.name"
    v-slot="aria"
    :label="$t(parameter.type === 'AddressList' ? 'rules.templates.parameters.addressList' : `rules.templates.parameters.${parameter.name}`)"
    :error="errors?.[parameter.name]"
  >
    <Select v-if="parameter.type === 'DomainList' || parameter.type === 'AddressList'" v-model="values[parameter.name]">
      <SelectTrigger :id="`${idPrefix}-${parameter.name}`" class="w-full" v-bind="aria">
        <SelectValue :placeholder="choices(parameter.type).length ? $t('rules.value.chooseList') : $t('rules.value.noLists')" />
      </SelectTrigger>
      <SelectContent>
        <SelectItem v-for="list in choices(parameter.type)" :key="list.id" :value="list.id">{{ list.name }}</SelectItem>
      </SelectContent>
    </Select>
    <Input
      v-else
      :id="`${idPrefix}-${parameter.name}`"
      v-model="values[parameter.name]"
      v-bind="aria"
      :type="parameter.type === 'Folder' ? 'text' : 'number'"
      :class="{ 'font-mono': parameter.type === 'Folder' }"
    />
  </FormField>
</template>
