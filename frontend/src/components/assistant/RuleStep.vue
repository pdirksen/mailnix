<script setup lang="ts">
import TemplateParameterFields from '@/components/rules/TemplateParameterFields.vue'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Textarea } from '@/components/ui/textarea'
import { activeLists, type AssistantState, type Blueprint, type BlueprintRule, isRuleAvailable, splitEntries } from '@/rules/assistant'
import type { Analysis } from '@/stores/assistant'
import { useListsStore } from '@/stores/lists'
import { Loader2Icon, PlusIcon, ScanSearchIcon } from '@lucide/vue'
import { computed } from 'vue'

const props = defineProps<{
  blueprint: Blueprint
  analysis: Analysis | null
  /** The selection changed since the analysis. */
  stale: boolean
  analyzing: boolean
  errors: Record<string, string>
}>()
const state = defineModel<AssistantState>({ required: true })
const emit = defineEmits<{ analyze: [] }>()

const lists = useListsStore()

const results = computed(() => new Map((props.analysis?.rules ?? []).map((r) => [r.key, r])))
const neededLists = computed(() => activeLists(state.value, props.blueprint))
/**
 * New domain lists that unsorted sender domains can be added to (Clients, Senders). Not the social networks: most
 * frequent senders are not one, so offering it on every row would only mislead.
 */
const domainTargets = computed(() => neededLists.value.filter((l) => l.kind === 'Domain' && l.listId === null && l.key !== 'social'))

/** Rules for older mail (e.g. the archive) find nothing when every sampled message is younger. */
function onlyOlderMail(rule: BlueprintRule) {
  return rule.parameters.some((p) => p.type === 'Days') && results.value.get(rule.key)?.matched === 0
}

function choice(rule: BlueprintRule) {
  return state.value.rules.find((r) => r.key === rule.key)!
}

function ruleErrors(key: string) {
  return Object.entries(props.errors)
    .filter(([path]) => path.startsWith(`rules.${key}.`) || path === `rules.${key}`)
    .map(([, message]) => message)
}

function listErrors(key: string) {
  return Object.entries(props.errors)
    .filter(([path]) => path === `lists.${key}` || path.startsWith(`lists.${key}.`))
    .map(([, message]) => message)
}

function existingLists(kind: string) {
  return lists.lists.filter((l) => (kind === 'Address' ? l.kind === 'Address' : l.kind !== 'Address'))
}

function selectList(key: string, value: unknown) {
  const list = state.value.lists.find((l) => l.key === key)!
  list.listId = value === '__new__' || typeof value !== 'string' ? null : value
}

function addDomain(listKey: string, domain: string) {
  const list = state.value.lists.find((l) => l.key === listKey)!
  if (!splitEntries(list.entries).includes(domain)) list.entries = [list.entries.trim(), domain].filter(Boolean).join('\n')
}

function inList(listKey: string, domain: string) {
  return splitEntries(state.value.lists.find((l) => l.key === listKey)!.entries).includes(domain)
}
</script>

<template>
  <div class="grid gap-6">
    <div class="flex flex-wrap items-center justify-between gap-3 rounded-lg border p-3">
      <p class="text-muted-foreground text-sm">
        <template v-if="analysis">
          {{
            $t('assistant.ruleStep.summary', {
              examined: analysis.examined,
              folder: analysis.folder,
              existing: analysis.sortedByExistingRules,
              unsorted: analysis.unsorted,
            })
          }}
          <span v-if="stale" class="text-warning ml-1">{{ $t('assistant.ruleStep.stale') }}</span>
        </template>
        <template v-else>{{ $t('assistant.ruleStep.analyzeHint') }}</template>
      </p>
      <Button variant="outline" :disabled="analyzing" data-testid="assistant-analyze" @click="emit('analyze')">
        <Loader2Icon v-if="analyzing" class="animate-spin" />
        <ScanSearchIcon v-else />
        {{ analysis ? $t('assistant.ruleStep.analyzeAgain') : $t('assistant.ruleStep.analyze') }}
      </Button>
    </div>

    <ul class="grid gap-2" data-testid="assistant-rules">
      <li v-for="rule in blueprint.rules" :key="rule.key" class="grid gap-3 rounded-lg border p-3" :class="{ 'opacity-60': !isRuleAvailable(state, rule) }">
        <div class="flex items-start gap-3">
          <Checkbox
            :id="`rule-${rule.key}`"
            v-model="choice(rule).enabled"
            class="mt-0.5"
            :disabled="!isRuleAvailable(state, rule)"
            :data-testid="`rule-toggle-${rule.key}`"
          />
          <Label :for="`rule-${rule.key}`" class="grid flex-1 gap-1 font-normal">
            <span class="font-medium">{{ $t(`rules.templates.items.${rule.templateId}.name`) }}</span>
            <span class="text-muted-foreground text-sm">{{ $t(`rules.templates.items.${rule.templateId}.description`) }}</span>
            <span v-if="!isRuleAvailable(state, rule)" class="text-muted-foreground text-xs">{{ $t('assistant.ruleStep.folderOff') }}</span>
          </Label>
          <div v-if="results.get(rule.key) && choice(rule).enabled" class="flex flex-wrap justify-end gap-1" :data-testid="`rule-result-${rule.key}`">
            <Badge variant="secondary">{{ $t('assistant.ruleStep.matched', { count: results.get(rule.key)!.matched }) }}</Badge>
            <Badge v-if="results.get(rule.key)!.sorted" variant="secondary">
              {{ $t('assistant.ruleStep.sorted', { count: results.get(rule.key)!.sorted }) }}
            </Badge>
            <Badge v-if="results.get(rule.key)!.blocked" variant="outline">
              {{ $t('assistant.ruleStep.blocked', { count: results.get(rule.key)!.blocked }) }}
            </Badge>
          </div>
        </div>

        <template v-if="choice(rule).enabled && isRuleAvailable(state, rule)">
          <div v-if="rule.parameters.length" class="grid gap-3 pl-7 sm:grid-cols-2">
            <TemplateParameterFields v-model="choice(rule).parameters" :parameters="rule.parameters" :id-prefix="`rule-${rule.key}`" />
          </div>
          <p v-if="onlyOlderMail(rule)" class="text-muted-foreground pl-7 text-xs" :data-testid="`rule-older-${rule.key}`">
            {{ $t('assistant.ruleStep.olderOnly') }}
          </p>
          <ul v-if="results.get(rule.key)?.examples.length" class="text-muted-foreground grid gap-1 pl-7 text-xs">
            <li v-for="(example, i) in results.get(rule.key)!.examples" :key="i" class="truncate">
              <span class="text-foreground">{{ example.subject || $t('assistant.ruleStep.noSubject') }}</span>
              · {{ example.from }}
              <span v-if="example.targetFolder" class="font-mono">→ {{ example.targetFolder }}</span>
            </li>
          </ul>
        </template>
        <p v-for="message in ruleErrors(rule.key)" :key="message" class="text-destructive pl-7 text-sm" role="alert">{{ message }}</p>
      </li>
    </ul>

    <div v-if="neededLists.length" class="grid gap-3">
      <h3 class="font-medium">{{ $t('assistant.ruleStep.lists') }}</h3>
      <div v-for="list in neededLists" :key="list.key" class="grid gap-3 rounded-lg border p-3">
        <div class="grid gap-2 sm:grid-cols-2">
          <div class="grid gap-2">
            <Label :for="`list-${list.key}`">{{ $t(`assistant.listPurpose.${list.key}`) }}</Label>
            <Select :model-value="list.listId ?? '__new__'" @update:model-value="(value) => selectList(list.key, value)">
              <SelectTrigger :id="`list-${list.key}`" class="w-full">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="__new__">{{ $t('assistant.ruleStep.newList') }}</SelectItem>
                <SelectItem v-for="existing in existingLists(list.kind)" :key="existing.id" :value="existing.id">{{ existing.name }}</SelectItem>
              </SelectContent>
            </Select>
          </div>
          <div v-if="list.listId === null" class="grid gap-2">
            <Label :for="`list-${list.key}-name`">{{ $t('common.name') }}</Label>
            <Input :id="`list-${list.key}-name`" v-model="list.name" />
          </div>
        </div>
        <div v-if="list.listId === null" class="grid gap-2">
          <Label :for="`list-${list.key}-entries`">{{ $t(list.kind === 'Address' ? 'assistant.ruleStep.addresses' : 'assistant.ruleStep.domains') }}</Label>
          <Textarea :id="`list-${list.key}-entries`" v-model="list.entries" rows="4" class="font-mono text-sm" spellcheck="false" />
        </div>
        <p v-for="message in listErrors(list.key)" :key="message" class="text-destructive text-sm" role="alert">{{ message }}</p>
      </div>
    </div>

    <div v-if="analysis?.unsortedDomains.length" class="grid gap-3" data-testid="assistant-domains">
      <div>
        <h3 class="font-medium">{{ $t('assistant.ruleStep.unsortedTitle') }}</h3>
        <p class="text-muted-foreground text-sm">
          {{ $t(domainTargets.length ? 'assistant.ruleStep.unsortedHint' : 'assistant.ruleStep.unsortedHintNoList') }}
        </p>
      </div>
      <ul class="grid gap-1">
        <li v-for="domain in analysis.unsortedDomains" :key="domain.domain" class="flex flex-wrap items-center gap-2 rounded-md border px-3 py-1.5">
          <span class="font-mono text-sm">{{ domain.domain }}</span>
          <Badge variant="secondary">{{ domain.count }}</Badge>
          <span class="flex-1" />
          <Button
            v-for="target in domainTargets"
            :key="target.key"
            size="sm"
            variant="ghost"
            :disabled="inList(target.key, domain.domain)"
            @click="addDomain(target.key, domain.domain)"
          >
            <PlusIcon />
            {{ target.name }}
          </Button>
        </li>
      </ul>
    </div>
  </div>
</template>
