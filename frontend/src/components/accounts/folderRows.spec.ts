import type { FolderInfo } from '@/stores/accounts'
import { describe, expect, it } from 'vitest'
import { toFolderRows } from './folderRows'

function folder(path: string, parentPath: string | null, specialUse: FolderInfo['specialUse'] = null): FolderInfo {
  return { path, name: path.split('.').at(-1)!, parentPath, canSelect: true, specialUse, messages: null, unread: null }
}

describe('toFolderRows', () => {
  it('puts INBOX first and nests children under their parent', () => {
    const rows = toFolderRows([
      folder('Archive', null),
      folder('INBOX.Shopping.Invoices', 'INBOX.Shopping'),
      folder('INBOX', null, 'Inbox'),
      folder('INBOX.Shopping', 'INBOX'),
      folder('INBOX.Ads', 'INBOX'),
    ])

    expect(rows.map((r) => `${'  '.repeat(r.depth)}${r.folder.path}`)).toEqual([
      'INBOX',
      '  INBOX.Ads',
      '  INBOX.Shopping',
      '    INBOX.Shopping.Invoices',
      'Archive',
    ])
  })

  it('shows folders with an unknown parent at the top level', () => {
    const rows = toFolderRows([folder('Lost.Child', 'Lost')])

    expect(rows).toEqual([{ folder: expect.objectContaining({ path: 'Lost.Child' }), depth: 0 }])
  })
})
