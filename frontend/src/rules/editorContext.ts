import type { ListSummary } from '@/stores/lists'
import type { Rule } from '@/stores/rules'
import { inject, type InjectionKey, provide, type Ref } from 'vue'
import type { ConditionField, FieldDefinition } from './model'

/** Shared by all builder components of one rule editor (avoids passing these through every level). */
export interface RuleEditorContext {
  catalog: Ref<Map<ConditionField, FieldDefinition>>
  lists: Ref<ListSummary[]>
  /** Rules that may be referenced (all of the user's rules except the one being edited). */
  rules: Ref<Rule[]>
  /** Validation messages by path, e.g. "conditions.children[0].value"; empty while hidden. */
  errors: Ref<Record<string, string>>
  /** The account's folders (server paths) and hierarchy delimiter, for folder pickers. */
  folders: Ref<string[]>
  delimiter: Ref<string>
}

const KEY: InjectionKey<RuleEditorContext> = Symbol('rule-editor')

export function provideRuleEditor(context: RuleEditorContext) {
  provide(KEY, context)
}

export function useRuleEditor(): RuleEditorContext {
  const context = inject(KEY)
  if (!context) throw new Error('Rule editor components must be used inside the rule editor.')
  return context
}
