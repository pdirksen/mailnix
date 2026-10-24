import { expect, type Page, test } from '@playwright/test'
import { existsSync, readFileSync } from 'node:fs'
import path from 'node:path'
import { deleteAllAccounts } from './support'

/** Same credentials file as accounts.spec.ts. */
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
/** The only folder of the real mailbox that tests may change. */
const TEST_FOLDER = 'INBOX.mailnix-test'
const csrf = { 'X-Requested-With': 'mailnix' }

async function register(page: Page, prefix: string) {
  await page.goto('/register')
  const password = 'e2e password 123'
  await page.getByLabel('Email').fill(`${prefix}-${Date.now()}@example.com`)
  await page.getByLabel('Password', { exact: true }).fill(password)
  await page.getByLabel('Confirm password').fill(password)
  await page.getByRole('button', { name: 'Create account' }).click()
  await expect(page.getByRole('heading', { name: /Welcome/ })).toBeVisible()
}

function run(overrides: Record<string, unknown>) {
  return {
    id: '00000000-0000-0000-0000-0000000000aa',
    accountId: '00000000-0000-0000-0000-0000000000bb',
    accountName: 'Work',
    trigger: 'Manual',
    state: 'AwaitingConfirmation',
    folder: 'INBOX',
    messagesExamined: 500,
    messagesMatched: 2,
    plannedActions: 2,
    executedActions: 0,
    failedActions: 0,
    error: null,
    createdAt: new Date().toISOString(),
    startedAt: new Date().toISOString(),
    finishedAt: null,
    expiresAt: new Date(Date.now() + 86_400_000).toISOString(),
    undoneByRunId: null,
    undoRunId: null,
    undoRuleId: null,
    canUndo: false,
    ...overrides,
  }
}

test.afterEach(async ({ page }) => deleteAllAccounts(page))

test('a run waiting for confirmation shows up in the bell and can be reviewed and confirmed', async ({ page }) => {
  // The server side (safety gate) is covered by backend tests; here the API answers are simulated.
  const pending = run({})
  let state = 'AwaitingConfirmation'
  await page.route('**/api/runs?*', (route) => route.fulfill({ json: [run({ state })] }))
  await page.route(`**/api/runs/${pending.id}`, (route) =>
    route.fulfill({
      json: {
        run: run({ state }),
        actions: [],
        pending: [
          {
            uid: 1,
            from: 'news@shop.example',
            subject: 'Weekly deals',
            actions: [{ ruleName: 'Newsletters', type: 'MoveTo', targetFolder: 'INBOX.Newsletters', keyword: null, dryRun: false }],
          },
          {
            uid: 2,
            from: 'boss@example.com',
            subject: 'Quarterly report',
            actions: [{ ruleName: 'Flag boss', type: 'Flag', targetFolder: null, keyword: null, dryRun: false }],
          },
        ],
      },
    }),
  )
  await page.route(`**/api/runs/${pending.id}/confirm`, (route) => {
    state = 'Queued'
    return route.fulfill({ json: run({ state }) })
  })

  await register(page, 'activity')
  await expect(page.getByTestId('pending-count')).toHaveText('1')
  await page.getByTestId('pending-bell').click()
  await page.getByRole('link', { name: /Work · INBOX/ }).click()

  await expect(page.getByRole('heading', { name: 'Confirm run', level: 2 })).toBeVisible()
  await expect(page.getByText('This run would move 1 messages')).toBeVisible()
  await expect(page.getByTestId('pending-message')).toHaveCount(2)
  await expect(page.getByTestId('pending-message').first()).toContainText('Move to folder: INBOX.Newsletters')

  await page.getByTestId('confirm-run').click()
  await expect(page.getByText('Run confirmed.')).toBeVisible()
  await expect(page).toHaveURL(/\/activity\?run=/)
  await expect(page.getByTestId(`run-${pending.id}`)).toContainText('Queued')
  await expect(page.getByTestId('pending-count')).toHaveCount(0)
})

test('run rules now and undo a rule, with live status updates (real mailbox, test folder only)', async ({ page }) => {
  test.skip(!imap, 'No real IMAP credentials (test-email-credentials.ini)')
  test.setTimeout(120_000)
  await register(page, 'runs')

  // Watches only the test folder; the rule's condition matches nothing, so even a stray message there stays untouched.
  const account = await page.request.post('/api/accounts', {
    headers: csrf,
    data: { name: 'Real mailbox', ...imap, watchedFolders: [TEST_FOLDER] },
    timeout: 60_000,
  })
  expect(account.status()).toBe(201)
  const accountId = (await account.json()).id as string
  const rule = await page.request.post('/api/rules', {
    headers: csrf,
    data: {
      accountId,
      name: 'Flag e2e',
      sourceFolders: [TEST_FOLDER],
      conditions: { kind: 'condition', field: 'Subject', operator: 'Contains', value: 'mailnix-e2e-matches-nothing-7f3a' },
      actions: [{ type: 'Flag' }],
    },
  })
  expect(rule.status()).toBe(201)
  const nightly = await page.request.post('/api/rules', {
    headers: csrf,
    data: {
      accountId,
      name: 'Nightly e2e',
      trigger: 'Scheduled',
      schedule: '0 3 * * *',
      sourceFolders: [TEST_FOLDER],
      conditions: { kind: 'condition', field: 'Subject', operator: 'Contains', value: 'mailnix-e2e-nightly-7f3a' },
      actions: [{ type: 'Flag' }],
    },
  })
  expect(nightly.status()).toBe(201)

  await page.getByRole('link', { name: 'Activity' }).click()
  await expect(page.getByText('Nothing happened yet')).toBeVisible()

  await page.getByRole('link', { name: 'Rules', exact: true }).click()
  await expect(page.getByTestId('rule-Nightly e2e')).toContainText(/Scheduled · next in \d+ hours?/)
  await page.getByTestId('run-now').click()
  await expect(page.getByTestId('run-folder')).toContainText(TEST_FOLDER)
  await page.getByTestId('start-run').click()
  await page.getByRole('button', { name: 'Show' }).click()

  // The run finishes in the background; the list follows over the live connection without reloading.
  const runs = page.getByTestId('run-list').locator('> li')
  await expect(runs).toHaveCount(1)
  await expect(runs.first()).toContainText('Done', { timeout: 60_000 })
  await expect(runs.first()).toContainText(/0 actions on 0 of \d+ messages/)
  // Opened from the toast, the run's details are expanded; the mail server preselected the candidates.
  await expect(runs.first()).toContainText('No actions.')
  await expect(runs.first().getByTestId('search-filter')).toContainText('SUBJECT "mailnix-e2e-matches-nothing-7f3a" OR SUBJECT "mailnix-e2e-nightly-7f3a"')
  await runs.first().getByRole('button', { name: 'Details' }).click()
  await expect(runs.first()).not.toContainText('No actions.')

  await page.getByRole('link', { name: 'Rules', exact: true }).click()
  await page.getByTestId('rule-Flag e2e').getByRole('button').last().click()
  await page.getByRole('menuitem', { name: 'Undo recent actions' }).click()
  await page.getByTestId('start-undo-rule').click()
  await expect(page.getByText('Undo started.')).toBeVisible()

  await page.getByRole('link', { name: 'Activity' }).click()
  await expect(runs).toHaveCount(2)
  await expect(runs.first()).toContainText('Undo')
  await expect(runs.first()).toContainText('Done', { timeout: 60_000 })
})
