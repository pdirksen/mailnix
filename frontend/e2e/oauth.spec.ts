import { expect, type Page, test } from '@playwright/test'
import { FIRST_PAGE_LOAD } from './support'

async function register(page: Page) {
  await page.goto('/register')
  const password = 'e2e password 123'
  await page.getByLabel('Email').fill(`oauth-${Date.now()}@example.com`)
  await page.getByLabel('Password', { exact: true }).fill(password)
  await page.getByLabel('Confirm password').fill(password)
  await page.getByRole('button', { name: 'Create account' }).click()
  await expect(page.getByRole('heading', { name: /Welcome/ })).toBeVisible(FIRST_PAGE_LOAD)
}

const account = {
  id: '00000000-0000-0000-0000-00000000a001',
  name: 'Private Gmail',
  provider: 'Google',
  host: 'imap.gmail.com',
  port: 993,
  security: 'SslOnConnect',
  username: 'someone@gmail.com',
  enabled: true,
  status: 'Ok',
  lastErrorCode: null,
  lastError: null,
  lastCheckedAt: new Date().toISOString(),
  watchedFolders: ['INBOX'],
  specialFolders: { sent: '[Gmail]/Sent Mail', drafts: null, trash: '[Gmail]/Trash', junk: '[Gmail]/Spam', archive: '[Gmail]/All Mail' },
  capabilities: ['Idle', 'Move', 'UidPlus', 'GmailExtensions'],
  blastRadiusLimit: 200,
  purgeTrashAfterDays: null,
  lastPurgeAt: null,
  createdAt: new Date().toISOString(),
}

const folder = (path: string, specialUse: string | null = null) => ({
  path,
  name: path.split('/').pop(),
  parentPath: path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : null,
  canSelect: true,
  specialUse,
  messages: null,
  unread: null,
})

test('without OAuth apps configured, Google and Microsoft are offered as not set up', async ({ page }) => {
  await register(page)
  await page.goto('/accounts/new')

  await expect(page.getByTestId('provider-Google')).toBeDisabled()
  await expect(page.getByTestId('provider-Google')).toContainText('Not set up')
  await expect(page.getByTestId('provider-Microsoft')).toBeDisabled()
  await expect(page.getByTestId('provider-Imap')).toBeEnabled()
})

test('signing in with Google adds the account and opens its settings', async ({ page }) => {
  // The provider is simulated: it sends the browser straight back to /oauth/callback with a code.
  const completed: unknown[] = []
  await page.route('**/api/oauth/providers', (route) =>
    route.fulfill({ json: [{ provider: 'Google', configured: true }, { provider: 'Microsoft', configured: false }] }),
  )
  await page.route('**/api/oauth/google/start', (route) =>
    route.fulfill({ json: { authorizationUrl: '/oauth/callback?state=signed-state&code=one-time-code' } }),
  )
  await page.route('**/api/oauth/complete', async (route) => {
    completed.push(route.request().postDataJSON())
    await route.fulfill({ json: { account, created: true } })
  })
  await page.route(`**/api/accounts/${account.id}`, (route) => route.fulfill({ json: account }))
  await page.route(`**/api/accounts/${account.id}/folders?*`, (route) =>
    route.fulfill({
      json: {
        delimiter: '/',
        folders: [folder('INBOX', 'Inbox'), folder('[Gmail]/All Mail', 'All'), folder('[Gmail]/Trash', 'Trash'), folder('[Gmail]/Spam', 'Junk')],
        specialFolders: account.specialFolders,
      },
    }),
  )

  await register(page)
  await page.goto('/accounts/new')
  await page.getByTestId('provider-Google').click()
  await expect(page.getByText('mailnix never sees your Google password')).toBeVisible()
  await page.getByLabel(/Account name/).fill('Private Gmail')
  await page.getByTestId('oauth-Google').click()

  await expect(page.getByText('Account added. Check its folders and settings.')).toBeVisible()
  await expect(page).toHaveURL(new RegExp(`/accounts/${account.id}\\?tab=settings$`))
  expect(completed).toEqual([{ state: 'signed-state', code: 'one-time-code' }])
  await expect(page.getByText('Signed in with Google as someone@gmail.com.')).toBeVisible()
  await expect(page.getByTestId('oauth-Google')).toContainText('Reconnect with Google')
  await expect(page.getByLabel('IMAP server')).toHaveCount(0)
})
