import type { Schemas } from '@/api/client'
import { UNARY_OPERATORS } from './model'

type Translate = (key: string, named?: Record<string, unknown>) => string

export interface DescribeContext {
  t: Translate
  listName: (id: string) => string | undefined
  ruleName: (id: string) => string | undefined
}

const UNITS = [
  { unit: 'MB', size: 1024 * 1024 },
  { unit: 'KB', size: 1024 },
  { unit: 'B', size: 1 },
] as const

/** Splits a byte count into the largest whole unit: 2621440 → { value: 2.5, unit: 'MB' }. */
export function splitBytes(bytes: number): { value: number; unit: 'B' | 'KB' | 'MB' } {
  for (const { unit, size } of UNITS) {
    if (bytes >= size && (bytes / size) * 100 === Math.round((bytes / size) * 100)) {
      return { value: bytes / size, unit }
    }
  }
  return { value: bytes, unit: 'B' }
}

/** "Move to: Archive/2026", "Mark as read" */
export function describeAction(action: { type: string; targetFolder?: string | null; keyword?: string | null }, t: Translate): string {
  const target = action.targetFolder ?? action.keyword
  return target ? `${t(`rules.actions.${action.type}`)}: ${target}` : t(`rules.actions.${action.type}`)
}

export const UNIT_SIZES: Record<'B' | 'KB' | 'MB', number> = { B: 1, KB: 1024, MB: 1024 * 1024 }

/** "Sender domain is (incl. subdomains) shop.example", "Newsletter / bulk mail: yes" */
export function describeCondition(condition: Schemas['Condition'] | Schemas['ConditionNodeCondition'], context: DescribeContext): string {
  const { t } = context
  const field = condition.field === 'Header' ? `${t('rules.fields.Header')} ${condition.headerName ?? ''}`.trim() : t(`rules.fields.${condition.field}`)
  const operator = t(`rules.operators.${condition.operator}`)

  if (condition.operator === 'IsTrue' || condition.operator === 'IsFalse') return `${field}: ${operator}`
  if (UNARY_OPERATORS.includes(condition.operator)) return `${field} ${operator}`

  const raw = condition.value ?? ''
  let value: string
  switch (condition.operator) {
    case 'InList':
    case 'NotInList':
      value = `"${context.listName(raw) ?? raw}"`
      break
    case 'Matches':
    case 'NotMatches':
      value = `"${context.ruleName(raw) ?? raw}"`
      break
    default:
      if ((condition.field === 'Size' || condition.field === 'AttachmentSize') && raw !== '' && !Number.isNaN(Number(raw))) {
        const { value: amount, unit } = splitBytes(Number(raw))
        value = `${amount} ${t(`rules.value.units.${unit}`)}`
      } else if (condition.field === 'AgeDays') {
        value = `${raw} ${t('rules.value.days')}`
      } else {
        value = `"${raw}"`
      }
  }
  return `${field} ${operator} ${value}`
}
