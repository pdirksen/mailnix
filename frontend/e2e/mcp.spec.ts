import { type APIRequestContext, expect, type Page, test } from '@playwright/test'
import { createHash, randomBytes } from 'node:crypto'

const password = 'e2e password 123'

async function register(page: Page, email: string) {
  await page.goto('/register')
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password', { exact: true }).fill(password)
  await page.getByLabel('Confirm password').fill(password)
  await page.getByRole('button', { name: 'Create account' }).click()
  await expect(page.getByRole('heading', { name: /Welcome/ })).toBeVisible()
}

/** One JSON-RPC request to the MCP endpoint, as an MCP client sends it; the answer may come as an SSE event. */
async function mcp(request: APIRequestContext, token: string, method: string, params: object = {}) {
  const response = await request.post('/mcp', {
    headers: { Authorization: `Bearer ${token}`, Accept: 'application/json, text/event-stream', 'MCP-Protocol-Version': '2025-06-18' },
    data: { jsonrpc: '2.0', id: 1, method, params },
  })
  const text = await response.text()
  const json = text.startsWith('{') ? text : text.split('\n').find((line) => line.startsWith('data:'))?.slice(5)
  return { status: response.status(), body: json ? JSON.parse(json) : undefined }
}

const initialize = { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'e2e', version: '1' } }

test('a personal token gives an MCP client read access until it is revoked', async ({ page }) => {
  await register(page, `mcp-token-${Date.now()}@example.com`)
  await page.goto('/settings/assistants')
  await expect(page.getByTestId('mcp-url')).toContainText('/mcp')

  await page.getByTestId('create-token').click()
  await page.getByLabel('Name').fill('E2E client')
  await page.getByTestId('create-token-submit').click()
  const token = (await page.getByTestId('created-token').locator('code').textContent())!.trim()
  expect(token).toMatch(/^mnx_pat_/)
  await expect(page.getByTestId('snippet-claudeCode')).toContainText(`Authorization: Bearer ${token}`)
  await page.getByRole('button', { name: 'Done' }).click()

  const init = await mcp(page.request, token, 'initialize', initialize)
  expect(init.status).toBe(200)
  expect(init.body.result.serverInfo.name).toBe('mailnix')
  const tools = (await mcp(page.request, token, 'tools/list')).body.result.tools.map((t: { name: string }) => t.name)
  expect(tools).toContain('preview_rule')
  expect(tools).not.toContain('create_rule')

  await expect(page.getByTestId('token-list')).toContainText('E2E client')
  await page.getByRole('button', { name: 'Revoke' }).click()
  await page.getByTestId('confirm-revoke').click()
  await expect(page.getByTestId('tokens-empty')).toBeVisible()
  expect((await mcp(page.request, token, 'initialize', initialize)).status).toBe(401)
})

test('an app connects through sign-in and consent (OAuth with PKCE)', async ({ page }) => {
  await register(page, `mcp-oauth-${Date.now()}@example.com`)

  // The app registers itself and listens on a loopback address (answered here instead of by a real app).
  const redirectUri = 'http://127.0.0.1:9/callback'
  const registration = await page.request.post('/oauth2/register', { data: { client_name: 'E2E Assistant', redirect_uris: [redirectUri] } })
  expect(registration.status()).toBe(201)
  const clientId = (await registration.json()).client_id as string
  const callback = page.waitForRequest((r) => r.url().startsWith(redirectUri))
  await page.route(`${redirectUri}**`, (route) => route.fulfill({ body: 'connected' }))

  const verifier = randomBytes(32).toString('base64url')
  const challenge = createHash('sha256').update(verifier).digest('base64url')
  const query = new URLSearchParams({
    response_type: 'code',
    client_id: clientId,
    redirect_uri: redirectUri,
    code_challenge: challenge,
    code_challenge_method: 'S256',
    state: 'e2e-state',
    scope: 'mailnix.read',
  })
  await page.goto(`/oauth2/authorize?${query}`)
  await expect(page.getByTestId('connect-app')).toHaveText('E2E Assistant')
  await expect(page.getByText('you return to 127.0.0.1')).toBeVisible()
  await page.getByTestId('allow-changes').click()
  await page.getByTestId('connect-allow').click()

  const answer = new URL((await callback).url())
  expect(answer.searchParams.get('state')).toBe('e2e-state')
  const tokens = await page.request.post('/oauth2/token', {
    form: { grant_type: 'authorization_code', client_id: clientId, code: answer.searchParams.get('code')!, redirect_uri: redirectUri, code_verifier: verifier },
  })
  expect(tokens.status()).toBe(200)
  const issued = await tokens.json()
  expect(issued.scope).toBe('mailnix.read mailnix.write')

  const tools = (await mcp(page.request, issued.access_token, 'tools/list')).body.result.tools.map((t: { name: string }) => t.name)
  expect(tools).toContain('create_rule')

  await page.goto('/settings/assistants')
  await expect(page.getByTestId('token-list')).toContainText('E2E Assistant')
  await expect(page.getByTestId('token-list')).toContainText('Connected app')
  await page.getByRole('button', { name: 'Disconnect' }).click()
  await page.getByTestId('confirm-revoke').click()
  await expect(page.getByTestId('tokens-empty')).toBeVisible()
  expect((await mcp(page.request, issued.access_token, 'tools/list')).status).toBe(401)
})
