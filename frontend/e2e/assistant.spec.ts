import { expect, type Page, test } from '@playwright/test'
import { existsSync, readFileSync } from 'node:fs'
import path from 'node:path'
import { deleteAllAccounts, FIRST_PAGE_LOAD, pauseAccount } from './support'

/** Same credentials file as rules.spec.ts. */
function loadImapCredentials() {
  const file = process.env.MAILNIX_TEST_IMAP_CREDENTIALS ?? path.resolve(import.meta.dirname, '../../test-email-credentials.ini')
  if (!existsSync(file)) return null
  const values = Object.fromEntries(
    readFileSync(file, 'utf8')
      .split(/\r?\n/)
      .filter((line) => line.includes('=') && !/^\s*[#;[]/.test(line))
      .map((line) => [line.slice(0, line.indexOf('=')).trim().toLowerCase(), line.slice(line.indexOf('=') + 1)]),
  )
  const security = values.security?.trim().toUpperCase()
  return {
    host: values.host!.trim(),
    port: Number(values.port?.trim() ?? '993'),
    security: security === 'STARTTLS' ? 'StartTls' : security === 'NONE' ? 'None' : 'SslOnConnect',
    username: values.user!.trim(),
    password: values.password!,
  }
}

const imap = loadImapCredentials()

/** Folders are only ever created here (inside the agreed test area); reruns reuse them. */
const ROOT = 'INBOX/mailnix-test/assistant'

/** Screenshots of each step, for looking at the assistant without running it. */
const shots = process.env.MAILNIX_E2E_SCREENSHOTS

async function shot(page: Page, name: string) {
  if (shots) await page.screenshot({ path: path.join(shots, `assistant-${name}.png`), fullPage: true })
}

async function setUp(page: Page) {
  await page.goto('/register')
  const password = 'e2e password 123'
  await page.getByLabel('Email').fill(`assistant-${Date.now()}@example.com`)
  await page.getByLabel('Password', { exact: true }).fill(password)
  await page.getByLabel('Confirm password').fill(password)
  await page.getByRole('button', { name: 'Create account' }).click()
  await expect(page.getByRole('heading', { name: /Welcome/ })).toBeVisible(FIRST_PAGE_LOAD)

  const response = await page.request.post('/api/accounts', {
    headers: { 'X-Requested-With': 'mailnix' },
    data: { name: 'Real mailbox', ...imap, watchedFolders: ['INBOX'] },
    timeout: 60_000,
  })
  expect(response.status()).toBe(201)
  // It watches the real INBOX: paused, so the rules created below never act on mail. Analyzing only reads.
  const id = (await response.json()).id as string
  await pauseAccount(page, id)
  return id
}

test.afterEach(async ({ page }) => deleteAllAccounts(page))

test('the setup assistant tries a blueprint on the real inbox and creates rules, lists and folders', async ({ page }) => {
  test.skip(!imap, 'No real IMAP credentials (test-email-credentials.ini)')
  test.setTimeout(180_000)
  const accountId = await setUp(page)

  await page.goto(`/accounts/${accountId}`)
  await page.getByTestId('open-assistant').click()
  await expect(page.getByRole('heading', { level: 2, name: 'Setup assistant' })).toBeVisible()
  await shot(page, '1-blueprints')

  // Folders: below the test area instead of the inbox.
  await page.getByTestId('blueprint-by-type').click()
  await page.getByLabel('Place folders').click()
  await page.getByRole('option', { name: 'Below another folder' }).click()
  await page.getByLabel('Parent folder').fill(ROOT)
  await expect(page.getByTestId('assistant-folders')).toContainText('INBOX.mailnix-test.assistant.Finance.Invoices')
  await shot(page, '2-folders')
  await page.getByTestId('assistant-next').click()

  // Rules, tried on the newest messages of the real inbox.
  await page.getByTestId('assistant-analyze').click()
  await expect(page.getByText(/Of the newest \d+ messages in INBOX/)).toBeVisible({ timeout: 90_000 })
  await expect(page.getByTestId('rule-result-newsletters')).toContainText(/\d+ matches/)
  await shot(page, '3-rules')
  await page.getByTestId('assistant-next').click()

  // Create in dry-run mode, with the folders; the paused account cannot run anything.
  await expect(page.getByTestId('assistant-dry-run')).toHaveAttribute('data-state', 'checked')
  await expect(page.getByTestId('assistant-run')).toBeDisabled()
  await shot(page, '4-review')
  await page.getByTestId('assistant-apply').click()
  await expect(page.getByTestId('assistant-done')).toContainText(/\d+ rules created/, { timeout: 60_000 })
  await expect(page.getByTestId('assistant-done')).toContainText('One list created.')
  await expect(page.getByTestId('assistant-done')).not.toContainText('could not be created')
  await shot(page, '5-done')

  const rules = (await (await page.request.get(`/api/rules?accountId=${accountId}`)).json()) as { name: string; dryRun: boolean }[]
  expect(rules.length).toBeGreaterThan(5)
  expect(rules.every((r) => r.dryRun)).toBe(true)
  const folders = (await (await page.request.get(`/api/accounts/${accountId}/folders?counts=false`, { timeout: 60_000 })).json()) as {
    folders: { path: string }[]
  }
  expect(folders.folders.map((f) => f.path)).toContain('INBOX.mailnix-test.assistant.Finance.Invoices')

  // Going live is safe here: the account is paused.
  await page.getByTestId('assistant-go-live').click()
  await expect(page.getByText('The rules are live.')).toBeVisible()
  const live = (await (await page.request.get(`/api/rules?accountId=${accountId}`)).json()) as { dryRun: boolean }[]
  expect(live.every((r) => !r.dryRun)).toBe(true)
})
