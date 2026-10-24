import { expect, type Page, test } from '@playwright/test'
import { existsSync, readFileSync } from 'node:fs'
import path from 'node:path'
import { deleteAllAccounts, pauseAccount } from './support'

/** Same credentials file as accounts.spec.ts; the preview only reads from the mailbox. */
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

async function setUp(page: Page) {
  await page.goto('/register')
  const password = 'e2e password 123'
  await page.getByLabel('Email').fill(`rules-${Date.now()}@example.com`)
  await page.getByLabel('Password', { exact: true }).fill(password)
  await page.getByLabel('Confirm password').fill(password)
  await page.getByRole('button', { name: 'Create account' }).click()
  await expect(page.getByRole('heading', { name: /Welcome/ })).toBeVisible()

  // The browser session's cookies apply to page.request.
  const response = await page.request.post('/api/accounts', {
    headers: { 'X-Requested-With': 'mailnix' },
    data: { name: 'Real mailbox', ...imap, watchedFolders: ['INBOX'] },
    timeout: 60_000,
  })
  expect(response.status()).toBe(201)
  // It watches the real INBOX: paused, so the rules saved below never act on incoming mail. Previews still work.
  await pauseAccount(page, (await response.json()).id)
}

test.afterEach(async ({ page }) => deleteAllAccounts(page))

test('build rules with the template gallery and the editor, preview them on real mail, reorder and delete', async ({ page }) => {
  test.skip(!imap, 'No real IMAP credentials (test-email-credentials.ini)')
  test.setTimeout(120_000)
  await setUp(page)

  // 1. From a template, with a live preview against the real inbox.
  await page.getByRole('link', { name: 'Rules' }).click()
  await expect(page.getByText('No rules for this account yet')).toBeVisible()
  await page.getByTestId('open-templates').click()
  await page.getByTestId('template-newsletters-to-folder').click()
  await page.getByTestId('use-template').click()

  await expect(page.getByLabel('Name', { exact: true })).toHaveValue('Newsletters into a folder')
  await expect(page.getByTestId('folder-example')).toContainText('INBOX.Newsletters')
  await page.getByTestId('run-preview').click()
  await expect(page.getByTestId('preview-summary')).toContainText(/\d+ of \d+ messages match/, { timeout: 60_000 })

  // Explanation for the first matching message.
  const first = page.getByTestId('preview-messages').locator('li').first()
  await first.getByRole('button').first().click()
  await expect(first.getByTestId('preview-details')).toContainText('Newsletter / bulk mail: yes')

  await page.getByTestId('save-rule').click()
  await expect(page.getByText('Rule created.')).toBeVisible()
  await expect(page).toHaveURL(/\/rules\/[0-9a-f-]+$/)

  // 2. A rule built from scratch; validation errors show only after trying to save.
  await page.getByRole('link', { name: 'Rules', exact: true }).first().click()
  await page.getByRole('link', { name: 'New rule' }).click()
  await page.getByTestId('save-rule').click()
  await expect(page.getByText('Fix the marked problems first.')).toBeVisible()
  await expect(page.getByText('Enter a value.')).toBeVisible()

  await page.getByLabel('Name', { exact: true }).fill('Flag invoices')
  await page.getByTestId('condition-row').getByRole('combobox').first().click()
  await page.getByRole('option', { name: 'Subject' }).click()
  await page.getByTestId('condition-row').getByRole('combobox').nth(1).click()
  await page.getByRole('option', { name: 'contains', exact: true }).click()
  await page.getByTestId('condition-row').getByRole('textbox').fill('Rechnung')
  await page.getByTestId('add-action').click()
  await page.getByRole('menuitem', { name: 'Flag', exact: true }).click()
  await expect(page.getByText('Enter a value.')).toHaveCount(0)
  await page.getByTestId('save-rule').click()
  await expect(page.getByText('Rule created.')).toBeVisible()
  await expect(page).toHaveURL(/\/rules\/[0-9a-f-]+$/)

  // 3. Back in the list: two rules, reorder by dragging, delete one.
  await page.getByRole('link', { name: 'Rules', exact: true }).first().click()
  const list = page.getByTestId('rule-list')
  await expect(list.locator('li')).toHaveCount(2)
  await expect(list.locator('li').first()).toContainText('Newsletters into a folder')

  await list.locator('li').nth(1).getByRole('button', { name: 'Drag to reorder' }).dragTo(list.locator('li').first().getByRole('button', { name: 'Drag to reorder' }))
  await expect(page.getByText('Order saved.')).toBeVisible()
  await page.reload()
  await expect(page.getByTestId('rule-list').locator('li').first()).toContainText('Flag invoices')

  await page.getByTestId('rule-Flag invoices').getByRole('button', { name: 'Edit' }).click()
  await page.getByRole('menuitem', { name: 'Delete' }).click()
  await page.getByRole('alertdialog').getByRole('button', { name: 'Delete' }).click()
  await expect(page.getByTestId('rule-list').locator('li')).toHaveCount(1)
})
