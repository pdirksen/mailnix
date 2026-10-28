/** Ready-to-paste setups for MCP clients that use a personal access token. */
export type McpClientKind = 'claudeCode' | 'vsCode' | 'other'

export const mcpClients: McpClientKind[] = ['claudeCode', 'vsCode', 'other']

export function mcpSnippet(kind: McpClientKind, url: string, token: string): string {
  const headers = { Authorization: `Bearer ${token}` }
  switch (kind) {
    case 'claudeCode':
      return `claude mcp add --transport http mailnix ${url} --header "Authorization: Bearer ${token}"`
    case 'vsCode':
      // .vscode/mcp.json, or "MCP: Add Server" in the command palette.
      return JSON.stringify({ servers: { mailnix: { type: 'http', url, headers } } }, null, 2)
    case 'other':
      return JSON.stringify({ mcpServers: { mailnix: { type: 'http', url, headers } } }, null, 2)
  }
}
