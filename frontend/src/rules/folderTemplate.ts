/** Placeholders the server understands in folder templates. */
export const FOLDER_PLACEHOLDERS = ['year', 'month', 'day', 'fromDomain', 'fromAddress', 'fromName', 'listId', 'plusTag'] as const

/** Example values used to show what a template resolves to. */
const EXAMPLE_VALUES: Record<string, string> = {
  year: String(new Date().getFullYear()),
  month: String(new Date().getMonth() + 1).padStart(2, '0'),
  day: String(new Date().getDate()).padStart(2, '0'),
  fromDomain: 'shop.example',
  fromAddress: 'news@shop.example',
  fromName: 'Example Shop',
  listId: 'news.shop.example',
  plusTag: 'shop',
}

function sanitize(value: string, delimiter: string): string {
  const cleaned = [...value.trim()]
    .map((c) => (c === delimiter || '/\\*%'.includes(c) || c.charCodeAt(0) < 32 ? '_' : c))
    .join('')
    .trim()
  return cleaned.length === 0 ? '_' : cleaned
}

/**
 * Resolves a folder template the way the server does (see FolderTemplate.cs), using example values:
 * "/" separates levels; placeholder values are sanitized so they never add levels.
 * Returns null for unknown placeholders or unbalanced braces.
 */
export function resolveExample(template: string, delimiter: string): string | null {
  const segments = template.split('/')
  const resolved: string[] = []
  for (const segment of segments) {
    let result = ''
    let rest = segment
    while (rest.length > 0) {
      const open = rest.indexOf('{')
      const close = rest.indexOf('}')
      if (open === -1) {
        if (close !== -1) return null
        result += rest
        break
      }
      if (close < open) return null
      const name = rest.slice(open + 1, close).trim()
      if (!(name in EXAMPLE_VALUES)) return null
      result += rest.slice(0, open) + sanitize(EXAMPLE_VALUES[name]!, delimiter)
      rest = rest.slice(close + 1)
    }
    resolved.push(result.trim())
  }
  return resolved.join(delimiter)
}

/** Converts a server folder path ("INBOX.Shops") to template notation ("INBOX/Shops"). */
export function pathToTemplate(path: string, delimiter: string): string {
  return delimiter === '/' ? path : path.split(delimiter).join('/')
}
