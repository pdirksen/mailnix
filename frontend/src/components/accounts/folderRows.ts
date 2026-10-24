import type { FolderInfo } from '@/stores/accounts'

export interface FolderRow {
  folder: FolderInfo
  depth: number
}

/**
 * Flattens the server's folder list into display order: INBOX first, then the rest alphabetically,
 * each folder followed by its children. Folders whose parent is missing from the list appear at the top level.
 */
export function toFolderRows(folders: readonly FolderInfo[]): FolderRow[] {
  const known = new Set(folders.map((f) => f.path))
  const children = new Map<string | null, FolderInfo[]>()
  for (const folder of folders) {
    const parent = folder.parentPath && known.has(folder.parentPath) ? folder.parentPath : null
    children.set(parent, [...(children.get(parent) ?? []), folder])
  }

  const order = (a: FolderInfo, b: FolderInfo) =>
    Number(b.specialUse === 'Inbox') - Number(a.specialUse === 'Inbox') || a.name.localeCompare(b.name)

  const rows: FolderRow[] = []
  const visit = (parent: string | null, depth: number) => {
    for (const folder of [...(children.get(parent) ?? [])].sort(order)) {
      rows.push({ folder, depth })
      visit(folder.path, depth + 1)
    }
  }
  visit(null, 0)
  return rows
}
