import { describe, expect, it } from 'vitest'
import { type Blueprint, folderTemplate, initialState, splitEntries, staticPath, toSelection } from './assistant'

const t = (key: string) => key.split('.').pop()!

const blueprint: Blueprint = {
  id: 'business',
  folders: [
    { key: 'clients', suffix: '{fromDomain}', defaultOn: true },
    { key: 'fyi', suffix: null, defaultOn: false },
  ],
  rules: [
    { key: 'vip', templateId: 'vip-flag', folder: null, list: 'vip', defaultOn: true, parameters: [] },
    { key: 'clients', templateId: 'sort-by-sender-domain', folder: 'clients', list: 'clients', defaultOn: true, parameters: [] },
    { key: 'fyi', templateId: 'cc-only-fyi', folder: 'fyi', list: null, defaultOn: true, parameters: [] },
  ],
  lists: [
    { key: 'clients', kind: 'Domain', entries: [] },
    { key: 'vip', kind: 'Address', entries: ['boss@example.com'] },
  ],
}

describe('assistant selection', () => {
  it('joins root, name and suffix into a folder template', () => {
    expect(folderTemplate('INBOX', { name: ' Finance/Invoices/ ', suffix: null })).toBe('INBOX/Finance/Invoices')
    expect(folderTemplate('', { name: 'Archive', suffix: '{year}' })).toBe('Archive/{year}')
    expect(folderTemplate('INBOX/', { name: '/Shops', suffix: '{plusTag}' })).toBe('INBOX/Shops/{plusTag}')
  })

  it('finds the part of a template that can be created in advance', () => {
    expect(staticPath('INBOX/Archive/{year}', '.')).toBe('INBOX.Archive')
    expect(staticPath('{fromDomain}', '/')).toBeNull()
  })

  it('splits list entries on lines, commas and semicolons', () => {
    expect(splitEntries('a.example\n b.example, c.example;;\n')).toEqual(['a.example', 'b.example', 'c.example'])
  })

  it('leaves out rules whose folder is off and creates or reuses their lists', () => {
    const state = initialState(blueprint, t)
    state.lists.find((l) => l.key === 'clients')!.listId = 'existing-list'

    expect(toSelection(state, blueprint, t)).toEqual({
      blueprintId: 'business',
      folders: { clients: 'INBOX/clients/{fromDomain}' },
      rules: [
        { key: 'vip', name: 'name', parameters: {} },
        { key: 'clients', name: 'name', parameters: {} },
      ],
      lists: { vip: { name: 'vip', entries: ['boss@example.com'] }, clients: { listId: 'existing-list' } },
    })
  })
})
