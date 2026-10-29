import { expect, type Page, test } from '@playwright/test'
import { existsSync, readFileSync } from 'node:fs'
import path from 'node:path'
import { deleteAllAccounts, FIRST_PAGE_LOAD } from './support'

/**
 * Real mailbox credentials (read-only use: connection test, folder listing) from the git-ignored
 * test-email-credentials.ini in the repository root, or the file named in MAILNIX_TEST_IMAP_CREDENTIALS.
 */
function loadImapCredentials() {
  const file = process.env.MAILNIX_TEST_IMAP_CREDENTIALS ?? path.resolve(import.meta.dirname, '../../test-email-credentials.ini')
  if (!existsSync(file)) return null
  const values = Object.fromEntries(
    readFileSync(file, 'utf8')
      .split(/\r?\n/)
      .filter((line) => line.includes('=') && !/^\s*[#;[]/.test(line))
      .map((line) => [line.slice(0, line.indexOf('=')).trim().toLowerCase(), line.slice(line.indexOf('=') + 1)]),
  )
  return {
    host: values.host!.trim(),
    port: values.port?.trim() ?? '993',
    starttls: values.security?.trim().toUpperCase() === 'STARTTLS',
    user: values.user!.trim(),
    password: values.password!,
  }
}

const imap = loadImapCredentials()

async function registerNewUser(page: Page) {
  await page.goto('/register')
  const password = 'e2e password 123'
  await page.getByLabel('Email').fill(`e2e-${Date.now()}-${Math.random().toString(36).slice(2, 6)}@example.com`)
  await page.getByLabel('Password', { exact: true }).fill(password)
  await page.getByLabel('Confirm password').fill(password)
  await page.getByRole('button', { name: 'Create account' }).click()
  await expect(page.getByRole('heading', { name: /Welcome/ })).toBeVisible(FIRST_PAGE_LOAD)
}

// Normally deleted by the test itself; this covers failures half-way.
test.afterEach(async ({ page }) => deleteAllAccounts(page))

test('shows a translated error when the server cannot be reached', async ({ page }) => {
  await registerNewUser(page)
  await page.getByRole('link', { name: 'Accounts' }).click()
  await page.getByRole('link', { name: 'Add account' }).click()
  await page.getByTestId('provider-Imap').click()

  await page.getByLabel('IMAP server').fill('127.0.0.1')
  await page.getByLabel('Port', { exact: true }).fill('1')
  await page.getByLabel('Username').fill('nobody')
  await page.getByLabel('Password', { exact: true }).fill('irrelevant')
  await page.getByRole('button', { name: 'Next' }).click()

  await expect(page.getByTestId('connection-error')).toContainText('The server refused the connection')
  await expect(page.getByRole('button', { name: 'Next' })).toBeDisabled()
})

test('adds a real IMAP account, shows its folders and deletes it again', async ({ page }) => {
  test.skip(!imap, 'No real IMAP credentials (test-email-credentials.ini)')
  test.setTimeout(90_000)
  await registerNewUser(page)

  await page.goto('/accounts')
  await expect(page.getByText('No accounts yet')).toBeVisible()
  await page.getByRole('link', { name: 'Add account' }).click()
  await page.getByTestId('provider-Imap').click()

  await page.getByLabel('Account name').fill('E2E real mailbox')
  await page.getByLabel('IMAP server').fill(imap!.host)
  if (imap!.starttls) await page.getByRole('radio', { name: /^STARTTLS/ }).click()
  await page.getByLabel('Port', { exact: true }).fill(imap!.port)
  await page.getByLabel('Username').fill(imap!.user)
  await page.getByLabel('Password', { exact: true }).fill(imap!.password)
  await page.getByRole('button', { name: 'Next' }).click()

  await expect(page.getByTestId('connection-ok')).toBeVisible({ timeout: 30_000 })
  await page.getByRole('button', { name: 'Next' }).click()

  // INBOX is preselected; the trash folder was detected.
  await expect(page.getByRole('checkbox', { name: /^INBOX$/ })).toBeChecked()
  await expect(page.locator('#special-trash')).not.toHaveText('Not set')
  await page.getByTestId('create-account').click()

  await expect(page.getByRole('heading', { name: 'E2E real mailbox' })).toBeVisible({ timeout: 30_000 })
  await expect(page.getByTestId('account-status').first()).toHaveText('Connected')

  await page.getByRole('tab', { name: 'Folders' }).click()
  await expect(page.getByTestId('folder-tree')).toContainText('INBOX')
  await expect(page.getByTestId('folder-tree')).toContainText(/messages/, { timeout: 30_000 })

  await page.getByRole('tab', { name: 'Settings' }).click()
  await page.getByLabel('Account name').fill('E2E renamed')
  await page.getByTestId('save-account').click()
  await expect(page.getByRole('heading', { name: 'E2E renamed' })).toBeVisible({ timeout: 30_000 })

  await page.getByRole('button', { name: 'Delete account' }).click()
  await page.getByRole('alertdialog').getByRole('button', { name: 'Delete account' }).click()
  await expect(page).toHaveURL(/\/accounts$/)
  await expect(page.getByText('No accounts yet')).toBeVisible()
})
