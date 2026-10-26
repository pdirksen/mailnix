import { api, type Schemas, unwrap } from '@/api/client'
import type { ConditionGroup, FieldDefinition, RuleAction, RuleTrigger } from '@/rules/model'
import { defineStore } from 'pinia'
import { ref } from 'vue'

export type Rule = Schemas['RuleResponse']
export type RuleTemplateInfo = Schemas['RuleTemplateInfo']
export type Preview = Schemas['PreviewResponse']
export type PreviewMessage = Schemas['PreviewMessage']
export type RuleValidationError = Schemas['RuleValidationError']

/** The editable part of a rule. */
export interface RuleDraft {
  name: string
  description: string
  enabled: boolean
  stopProcessing: boolean
  dryRun: boolean
  trigger: RuleTrigger
  schedule: string
  sourceFolders: string[]
  conditions: ConditionGroup
  actions: RuleAction[]
}

export const useRulesStore = defineStore('rules', () => {
  const rules = ref<Rule[]>([])
  const fieldCatalog = ref<FieldDefinition[]>([])
  const templates = ref<RuleTemplateInfo[]>([])

  /** A draft handed from the template gallery (or "duplicate") to the editor. */
  const pendingDraft = ref<RuleDraft | null>(null)

  async function fetchAll() {
    rules.value = unwrap(await api.GET('/api/rules'))
  }

  async function fetchOne(id: string) {
    return unwrap(await api.GET('/api/rules/{id}', { params: { path: { id } } }))
  }

  async function loadFieldCatalog() {
    if (fieldCatalog.value.length === 0) {
      fieldCatalog.value = unwrap(await api.GET('/api/rules/field-catalog')).fields
    }
    return fieldCatalog.value
  }

  async function loadTemplates() {
    if (templates.value.length === 0) {
      templates.value = unwrap(await api.GET('/api/rule-templates'))
    }
    return templates.value
  }

  function toBody(draft: RuleDraft) {
    return {
      name: draft.name.trim(),
      description: draft.description.trim() || null,
      enabled: draft.enabled,
      stopProcessing: draft.stopProcessing,
      dryRun: draft.dryRun,
      trigger: draft.trigger,
      schedule: draft.trigger === 'Scheduled' ? draft.schedule.trim() : null,
      sourceFolders: draft.sourceFolders,
      conditions: draft.conditions as Schemas['ConditionNode'],
      actions: draft.actions as Schemas['RuleAction'][],
    }
  }

  async function create(accountId: string, draft: RuleDraft) {
    const rule = unwrap(await api.POST('/api/rules', { body: { accountId, ...toBody(draft) } }))
    rules.value.push(rule)
    return rule
  }

  async function update(id: string, draft: RuleDraft) {
    const rule = unwrap(await api.PUT('/api/rules/{id}', { params: { path: { id } }, body: toBody(draft) }))
    rules.value = rules.value.map((r) => (r.id === id ? rule : r))
    return rule
  }

  async function remove(id: string) {
    unwrap(await api.DELETE('/api/rules/{id}', { params: { path: { id } } }))
    rules.value = rules.value.filter((r) => r.id !== id)
  }

  async function reorder(accountId: string, ruleIds: string[]) {
    unwrap(await api.POST('/api/rules/reorder', { body: { accountId, ruleIds } }))
    rules.value = rules.value.map((r) => (r.accountId === accountId ? { ...r, priority: ruleIds.indexOf(r.id) } : r))
  }

  async function setDryRun(ruleIds: string[], dryRun: boolean) {
    unwrap(await api.POST('/api/rules/dry-run', { body: { ruleIds, dryRun } }))
    rules.value = rules.value.map((r) => (ruleIds.includes(r.id) ? { ...r, dryRun } : r))
  }

  async function validate(accountId: string, draft: RuleDraft, ruleId?: string) {
    const body = toBody(draft)
    return unwrap(
      await api.POST('/api/rules/validate', {
        body: { accountId, ruleId: ruleId ?? null, conditions: body.conditions, actions: body.actions, trigger: body.trigger, schedule: body.schedule },
      }),
    )
  }

  async function preview(accountId: string, draft: RuleDraft, options: { ruleId?: string; folder?: string; limit: number; refresh?: boolean }) {
    const body = toBody(draft)
    return unwrap(
      await api.POST('/api/accounts/{id}/rules/preview', {
        params: { path: { id: accountId } },
        body: {
          conditions: body.conditions,
          actions: body.actions,
          name: body.name || null,
          stopProcessing: body.stopProcessing,
          ruleId: options.ruleId ?? null,
          folder: options.folder ?? null,
          limit: options.limit,
          refresh: options.refresh ?? false,
        },
      }),
    )
  }

  async function draftFromTemplate(templateId: string, name: string, parameters: Record<string, string>) {
    return unwrap(await api.POST('/api/rule-templates/{templateId}/draft', { params: { path: { templateId } }, body: { name, parameters } }))
  }

  return {
    rules,
    fieldCatalog,
    templates,
    pendingDraft,
    fetchAll,
    fetchOne,
    loadFieldCatalog,
    loadTemplates,
    create,
    update,
    remove,
    reorder,
    setDryRun,
    validate,
    preview,
    draftFromTemplate,
  }
})
