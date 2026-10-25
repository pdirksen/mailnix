import { expect, type Page, test } from '@playwright/test'
import { mockStats } from './fixtures/stats'

async function register(page: Page) {
  await page.goto('/register')
  const password = 'e2e password 123'
  await page.getByLabel('Email').fill(`stats-${Date.now()}-${Math.random().toString(36).slice(2, 6)}@example.com`)
  await page.getByLabel('Password', { exact: true }).fill(password)
  await page.getByLabel('Confirm password').fill(password)
  await page.getByRole('button', { name: 'Create account' }).click()
  await expect(page.getByRole('heading', { name: /Welcome/ })).toBeVisible()
}

test('a new user sees an empty dashboard, stats and activity (real server)', async ({ page }) => {
  await register(page)

  await expect(page.getByTestId('kpi-processed')).toContainText('0')
  await expect(page.getByTestId('hints')).toContainText('No rules yet')
  await expect(page.getByText('Add a mailbox to see what mailnix does in it.')).toBeVisible()

  await page.getByRole('link', { name: 'Stats' }).click()
  await expect(page.getByRole('heading', { name: 'Stats', level: 2 })).toBeVisible()
  await expect(page.getByText('Nothing in this period yet.').first()).toBeVisible()
  await page.getByLabel('Period').click()
  await page.getByRole('option', { name: 'Last 7 days' }).click()
  await expect(page).toHaveURL(/range=7d/)

  // The CSV download works with the session cookie.
  const csv = await page.request.get('/api/stats/export?section=rules')
  expect(csv.ok()).toBe(true)
  expect(await csv.text()).toContain('rule,account,enabled')

  await page.goto('/activity?view=actions')
  await expect(page.getByText('No actions.')).toBeVisible()
})

test('the dashboard shows numbers, charts with a table view, hints and the latest actions', async ({ page }) => {
  await mockStats(page)
  await register(page)

  await expect(page.getByTestId('kpi-processed')).toContainText('48')
  await expect(page.getByTestId('kpi-processed')).toContainText('-4')
  await expect(page.getByTestId('kpi-pending')).toContainText('1')
  await expect(page.getByTestId('hints')).toContainText('"Old newsletters to Trash" would have acted 17 times')
  await expect(page.getByRole('link', { name: 'Newsletters to folder' })).toBeVisible()
  await expect(page.locator('canvas').first()).toBeVisible()

  // Every chart has a table twin.
  await page.getByRole('button', { name: 'Table' }).first().click()
  await expect(page.getByRole('columnheader', { name: 'Moved' })).toBeVisible()
  await expect(page.getByRole('columnheader', { name: 'Trashed' })).toBeVisible()

  await expect(page.getByTestId('action-list')).toContainText('Weekly deals: 30% off')
  await page.getByRole('link', { name: 'All activity' }).click()
  await expect(page).toHaveURL(/\/activity\?view=actions/)
  await expect(page.getByTestId('action-list')).toContainText('Your invoice 2026-1042')
})

test('the stats page sorts rules and offers CSV downloads', async ({ page }) => {
  await mockStats(page)
  await register(page)
  await page.goto('/stats')

  const rules = page.getByTestId('rule-stats')
  await expect(rules.locator('tbody tr').first()).toContainText('Newsletters to folder')
  await expect(rules).toContainText('No matches')
  await rules.getByRole('button', { name: 'Rule' }).click()
  await expect(rules.locator('tbody tr').first()).toContainText('Auto-replies away')

  await expect(page.getByText('shop.example').first()).toBeVisible()
  await expect(page.getByText('suspicious.example').first()).toBeVisible()
  await expect(page.locator('a[download][href*="section=security"]')).toHaveCount(1)
})
