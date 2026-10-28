import { describe, expect, it } from 'vitest'
import { mcpSnippet } from './mcpConfig'

describe('mcpSnippet', () => {
  const url = 'https://mail.example.com/mcp'
  const token = 'mnx_pat_abc'

  it('builds the Claude Code command', () => {
    expect(mcpSnippet('claudeCode', url, token)).toBe(
      'claude mcp add --transport http mailnix https://mail.example.com/mcp --header "Authorization: Bearer mnx_pat_abc"',
    )
  })

  it('builds the VS Code and generic configurations', () => {
    expect(JSON.parse(mcpSnippet('vsCode', url, token))).toEqual({
      servers: { mailnix: { type: 'http', url, headers: { Authorization: 'Bearer mnx_pat_abc' } } },
    })
    expect(JSON.parse(mcpSnippet('other', url, token)).mcpServers.mailnix.url).toBe(url)
  })
})
