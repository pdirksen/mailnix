import { describe, expect, it } from 'vitest'
import { mergeEntries, parseEntryLines } from './listEntries'

describe('parseEntryLines', () => {
  it('reads one entry per line with optional notes', () => {
    const text = 'boss@example.com, my boss\n\n  # a comment\nalice@example.org\r\nshop.example;\tshop notes\nx@example.com\tvia tab\n'

    expect(parseEntryLines(text)).toEqual([
      { value: 'boss@example.com', note: 'my boss' },
      { value: 'alice@example.org', note: null },
      { value: 'shop.example', note: 'shop notes' },
      { value: 'x@example.com', note: 'via tab' },
    ])
  })

  it('ignores lines without a value', () => {
    expect(parseEntryLines(', just a note\n   \n')).toEqual([])
  })
})

describe('mergeEntries', () => {
  it('appends new entries and skips existing ones case-insensitively', () => {
    const result = mergeEntries([{ value: 'a@example.com', note: null }], [
      { value: 'A@Example.com', note: 'dup' },
      { value: 'b@example.com', note: null },
      { value: 'b@example.com', note: null },
    ])

    expect(result.entries.map((e) => e.value)).toEqual(['a@example.com', 'b@example.com'])
    expect(result.skipped).toBe(2)
  })
})
