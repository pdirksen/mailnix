import type { ListEntry } from '@/stores/lists'

/**
 * Parses pasted text into list entries: one entry per line, optionally followed by a note after
 * the first comma, semicolon or tab ("boss@example.com, my boss"). Blank lines and # comments are ignored.
 */
export function parseEntryLines(text: string): ListEntry[] {
  return text
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter((line) => line.length > 0 && !line.startsWith('#'))
    .map((line) => {
      const separator = line.search(/[,;\t]/)
      if (separator === -1) return { value: line, note: null }
      const note = line.slice(separator + 1).trim()
      return { value: line.slice(0, separator).trim(), note: note.length > 0 ? note : null }
    })
    .filter((entry) => entry.value.length > 0)
}

/** Appends entries, skipping values already present (case-insensitive). */
export function mergeEntries(existing: readonly ListEntry[], added: readonly ListEntry[]): { entries: ListEntry[]; skipped: number } {
  const seen = new Set(existing.map((e) => e.value.toLowerCase()))
  const entries = [...existing]
  let skipped = 0
  for (const entry of added) {
    const key = entry.value.toLowerCase()
    if (seen.has(key)) {
      skipped++
    } else {
      seen.add(key)
      entries.push(entry)
    }
  }
  return { entries, skipped }
}
