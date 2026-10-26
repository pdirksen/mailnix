import type { Schemas } from '@/api/client'

export type Blueprint = Schemas['BlueprintInfo']
export type BlueprintRule = Schemas['BlueprintRuleInfo']
export type AssistantSelection = Schemas['AssistantSelection']

type Translate = (key: string) => string

export interface FolderChoice {
  key: string
  enabled: boolean
  /** One or more levels below the root, e.g. "Finance/Invoices". */
  name: string
  /** Fixed levels with placeholders, e.g. "{year}". */
  suffix: string | null
}

export interface RuleChoice {
  key: string
  enabled: boolean
  /** Values for the template parameters that are not bound to a folder or list (e.g. days). */
  parameters: Record<string, string>
}

export interface ListChoice {
  key: string
  kind: Schemas['NamedListKind']
  /** An existing list; null creates a new one from name and entries. */
  listId: string | null
  name: string
  /** One entry per line. */
  entries: string
}

/** Everything the user picks in the assistant, before it is turned into an API selection. */
export interface AssistantState {
  blueprintId: string
  /** Levels above all folders, e.g. "INBOX"; empty for the top level. */
  root: string
  folders: FolderChoice[]
  rules: RuleChoice[]
  lists: ListChoice[]
}

export const ROOT_PRESETS = ['INBOX', ''] as const

export function initialState(blueprint: Blueprint, t: Translate, root = 'INBOX'): AssistantState {
  return {
    blueprintId: blueprint.id,
    root,
    folders: blueprint.folders.map((f) => ({ key: f.key, enabled: f.defaultOn, name: t(`assistant.folders.${f.key}`), suffix: f.suffix })),
    rules: blueprint.rules.map((r) => ({
      key: r.key,
      enabled: r.defaultOn,
      parameters: Object.fromEntries(r.parameters.map((p) => [p.name, p.default ?? ''])),
    })),
    lists: blueprint.lists.map((l) => ({ key: l.key, kind: l.kind, listId: null, name: t(`assistant.lists.${l.key}`), entries: l.entries.join('\n') })),
  }
}

/** The folder template for a folder, e.g. "INBOX/Archive/{year}". */
export function folderTemplate(root: string, folder: Pick<FolderChoice, 'name' | 'suffix'>): string {
  return [root, folder.name, folder.suffix ?? '']
    .map((part) => part.trim().replace(/^\/+|\/+$/g, ''))
    .filter((part) => part.length > 0)
    .join('/')
}

/** The server path up to the first placeholder (what can be created in advance); null if none. */
export function staticPath(template: string, delimiter: string): string | null {
  const segments: string[] = []
  for (const segment of template.split('/')) {
    if (segment.includes('{')) break
    segments.push(segment.trim())
  }
  return segments.length ? segments.join(delimiter) : null
}

/** A rule can only be used while its folder is selected. */
export function isRuleAvailable(state: AssistantState, rule: BlueprintRule): boolean {
  return rule.folder === null || state.folders.some((f) => f.key === rule.folder && f.enabled)
}

export function activeRules(state: AssistantState, blueprint: Blueprint): BlueprintRule[] {
  return blueprint.rules.filter((r) => isRuleAvailable(state, r) && state.rules.find((c) => c.key === r.key)?.enabled)
}

/** The lists the selected rules need. */
export function activeLists(state: AssistantState, blueprint: Blueprint): ListChoice[] {
  const keys = new Set(activeRules(state, blueprint).flatMap((r) => (r.list ? [r.list] : [])))
  return state.lists.filter((l) => keys.has(l.key))
}

export function splitEntries(text: string): string[] {
  return text
    .split(/[\n,;]/)
    .map((e) => e.trim())
    .filter((e) => e.length > 0)
}

export function toSelection(state: AssistantState, blueprint: Blueprint, t: Translate): AssistantSelection {
  return {
    blueprintId: state.blueprintId,
    folders: Object.fromEntries(state.folders.filter((f) => f.enabled).map((f) => [f.key, folderTemplate(state.root, f)])),
    rules: activeRules(state, blueprint).map((r) => ({
      key: r.key,
      name: t(`rules.templates.items.${r.templateId}.name`),
      parameters: state.rules.find((c) => c.key === r.key)?.parameters ?? {},
    })),
    lists: Object.fromEntries(
      activeLists(state, blueprint).map((l) => [l.key, l.listId ? { listId: l.listId } : { name: l.name.trim(), entries: splitEntries(l.entries) }]),
    ),
  }
}
