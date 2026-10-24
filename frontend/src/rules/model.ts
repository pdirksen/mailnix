import type { Schemas } from '@/api/client'

export type ConditionField = Schemas['ConditionField']
export type ConditionOperator = Schemas['ConditionOperator']
export type GroupOperator = Schemas['GroupOperator']
export type FieldDefinition = Schemas['FieldDefinition']
export type FieldCategory = Schemas['FieldCategory']
export type ActionType = Schemas['ActionType']
export type RuleTrigger = Schemas['RuleTrigger']
export type TraceNode = Schemas['TraceNode']

/** Condition tree as edited in the UI; same JSON shape as the API, with required discriminators. */
export interface Condition {
  kind: 'condition'
  field: ConditionField
  operator: ConditionOperator
  value?: string | null
  headerName?: string | null
  caseSensitive: boolean
}

export interface ConditionGroup {
  kind: 'group'
  operator: GroupOperator
  children: ConditionNode[]
}

export type ConditionNode = Condition | ConditionGroup

export type RuleAction =
  | { type: 'MarkRead' | 'MarkUnread' | 'Flag' | 'Unflag' | 'Delete' | 'MarkAsSpam' | 'Archive' }
  | { type: 'AddKeyword' | 'RemoveKeyword'; keyword: string }
  | { type: 'MoveTo' | 'CopyTo'; folder: string }

export const TERMINAL_ACTIONS: readonly ActionType[] = ['MoveTo', 'Delete', 'MarkAsSpam', 'Archive']
export const NON_TERMINAL_ACTIONS: readonly ActionType[] = ['MarkRead', 'MarkUnread', 'Flag', 'Unflag', 'AddKeyword', 'RemoveKeyword', 'CopyTo']

export const isTerminal = (action: Pick<RuleAction, 'type'>) => TERMINAL_ACTIONS.includes(action.type)

/** Operators without an operand. */
export const UNARY_OPERATORS: readonly ConditionOperator[] = ['Exists', 'NotExists', 'IsTrue', 'IsFalse']

/** Text operators where upper/lower case can matter. */
export const CASE_OPERATORS: readonly ConditionOperator[] = ['Equals', 'NotEquals', 'Contains', 'NotContains', 'StartsWith', 'EndsWith', 'Regex']

export function newAction(type: ActionType): RuleAction {
  switch (type) {
    case 'AddKeyword':
    case 'RemoveKeyword':
      return { type, keyword: '' }
    case 'MoveTo':
    case 'CopyTo':
      return { type, folder: '' }
    default:
      return { type } as RuleAction
  }
}

export function newCondition(definition: FieldDefinition): Condition {
  return {
    kind: 'condition',
    field: definition.field,
    operator: definition.operators[0]!,
    value: definition.valueType === 'Enum' ? (definition.options?.[0] ?? null) : null,
    headerName: definition.requiresHeaderName ? '' : null,
    caseSensitive: false,
  }
}

export function newGroup(operator: GroupOperator = 'All', children: ConditionNode[] = []): ConditionGroup {
  return { kind: 'group', operator, children }
}

/** Server JSON → editable model (fills defaults, always starts with a group so conditions can be added). */
export function fromApi(node: Schemas['ConditionNode']): ConditionGroup {
  const convert = (n: Schemas['ConditionNode']): ConditionNode =>
    'children' in n
      ? { kind: 'group', operator: n.operator, children: n.children.map(convert) }
      : { kind: 'condition', field: n.field, operator: n.operator, value: n.value ?? null, headerName: n.headerName ?? null, caseSensitive: n.caseSensitive ?? false }
  const root = convert(node)
  return root.kind === 'group' ? root : newGroup('All', [root])
}

export function actionsFromApi(actions: readonly Schemas['RuleAction'][]): RuleAction[] {
  return actions.map((a) => ({ ...a }) as RuleAction)
}

/** Number of conditions (leaves) in a tree. */
export function countConditions(node: ConditionNode): number {
  return node.kind === 'condition' ? 1 : node.children.reduce((sum, child) => sum + countConditions(child), 0)
}

/** Validation error path of a node, matching the server's ("conditions.children[1].children[0]"). */
export function childPath(parent: string, index: number) {
  return `${parent}.children[${index}]`
}

/** An editable copy of a saved rule. */
export function toDraft(rule: {
  name: string
  description?: string | null
  enabled: boolean
  stopProcessing: boolean
  dryRun: boolean
  trigger: RuleTrigger
  schedule?: string | null
  sourceFolders: string[]
  conditions: Schemas['ConditionNode']
  actions: Schemas['RuleAction'][]
}) {
  return {
    name: rule.name,
    description: rule.description ?? '',
    enabled: rule.enabled,
    stopProcessing: rule.stopProcessing,
    dryRun: rule.dryRun,
    trigger: rule.trigger,
    schedule: rule.schedule ?? '',
    sourceFolders: [...rule.sourceFolders],
    conditions: fromApi(rule.conditions),
    actions: actionsFromApi(rule.actions),
  }
}
