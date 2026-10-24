import { i18n } from '@/i18n'
import { describe, expect, it } from 'vitest'
import { describeCondition, splitBytes } from './describe'
import { pathToTemplate, resolveExample } from './folderTemplate'
import { countConditions, fromApi, isTerminal, newAction, toDraft } from './model'

const t = i18n.global.t as (key: string, named?: Record<string, unknown>) => string
const year = String(new Date().getFullYear())

describe('resolveExample', () => {
  it('fills placeholders and uses the server delimiter, like FolderTemplate.cs', () => {
    expect(resolveExample('INBOX/Archive/{year}', '.')).toBe(`INBOX.Archive.${year}`)
    expect(resolveExample('Domains/{fromDomain}', '.')).toBe('Domains.shop_example')
    expect(resolveExample('Domains/{ fromDomain }', '/')).toBe('Domains/shop.example')
  })

  it('returns null for invalid templates', () => {
    expect(resolveExample('Archive/{nope}', '.')).toBeNull()
    expect(resolveExample('Archive/{year', '.')).toBeNull()
    expect(resolveExample('Archive/year}', '.')).toBeNull()
  })

  it('converts server paths into template notation', () => {
    expect(pathToTemplate('INBOX.Shops.Amazon', '.')).toBe('INBOX/Shops/Amazon')
    expect(pathToTemplate('INBOX/Shops', '/')).toBe('INBOX/Shops')
  })
})

describe('describeCondition', () => {
  const context = { t, listName: (id: string) => (id === 'l1' ? 'VIP' : undefined), ruleName: () => 'Newsletters' }

  it.each([
    [{ field: 'FromDomain', operator: 'DomainMatches', value: 'shop.example' }, 'Sender domain is (incl. subdomains) "shop.example"'],
    [{ field: 'IsNewsletter', operator: 'IsTrue' }, 'Newsletter / bulk mail: yes'],
    [{ field: 'From', operator: 'InList', value: 'l1' }, 'Sender address is in list "VIP"'],
    [{ field: 'Rule', operator: 'NotMatches', value: 'r1' }, 'Rule does not match "Newsletters"'],
    [{ field: 'AttachmentSize', operator: 'GreaterThan', value: '10485760' }, 'Largest attachment more than 10 MB'],
    [{ field: 'AgeDays', operator: 'GreaterThan', value: '30' }, 'Age more than 30 days'],
    [{ field: 'Header', operator: 'Exists', headerName: 'X-Mailer' }, 'Header X-Mailer is present'],
  ] as const)('%o', (condition, expected) => {
    i18n.global.locale.value = 'en'
    expect(describeCondition({ caseSensitive: false, ...condition }, context)).toBe(expected)
  })
})

describe('splitBytes', () => {
  it.each([
    [10485760, { value: 10, unit: 'MB' }],
    [2621440, { value: 2.5, unit: 'MB' }],
    [2048, { value: 2, unit: 'KB' }],
    [1000, { value: 1000, unit: 'B' }],
  ])('%d', (bytes, expected) => expect(splitBytes(bytes)).toEqual(expected))
})

describe('model', () => {
  it('wraps a single condition from the API in a group so the editor can add more', () => {
    const root = fromApi({ kind: 'condition', field: 'Subject', operator: 'Contains', value: 'x', caseSensitive: false })
    expect(root).toEqual({
      kind: 'group',
      operator: 'All',
      children: [{ kind: 'condition', field: 'Subject', operator: 'Contains', value: 'x', headerName: null, caseSensitive: false }],
    })
    expect(countConditions(root)).toBe(1)
  })

  it('creates actions with their parameters', () => {
    expect(newAction('MoveTo')).toEqual({ type: 'MoveTo', folder: '' })
    expect(newAction('AddKeyword')).toEqual({ type: 'AddKeyword', keyword: '' })
    expect(isTerminal(newAction('Archive'))).toBe(true)
    expect(isTerminal(newAction('CopyTo'))).toBe(false)
  })

  it('turns a saved rule into an independent draft', () => {
    const rule = {
      name: 'R',
      description: null,
      enabled: true,
      stopProcessing: false,
      dryRun: true,
      trigger: 'Scheduled' as const,
      schedule: '0 3 * * *',
      sourceFolders: ['INBOX'],
      conditions: { kind: 'condition' as const, field: 'IsUnread' as const, operator: 'IsTrue' as const, caseSensitive: false },
      actions: [{ type: 'MarkRead' as const }],
    }
    const draft = toDraft(rule)
    draft.sourceFolders.push('Other')
    expect(rule.sourceFolders).toEqual(['INBOX'])
    expect(draft.description).toBe('')
    expect(draft.schedule).toBe('0 3 * * *')
  })
})
