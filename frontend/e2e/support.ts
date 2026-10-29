import type { Page } from '@playwright/test'

const csrf = { 'X-Requested-With': 'mailnix' }

/**
 * For the dashboard right after signing up: the first visit loads the chart library (~0.5 MB). On a freshly started
 * server with several browsers in parallel that can take longer than the default 5 seconds.
 */
export const FIRST_PAGE_LOAD = { timeout: 15_000 }

/**
 * The server's worker watches every enabled account. Tests that use the real mailbox therefore pause accounts that
 * watch real folders (so their rules never act on incoming mail) and delete all their accounts afterwards.
 */
export async function pauseAccount(page: Page, id: string) {
  const account = await (await page.request.get(`/api/accounts/${id}`)).json()
  const response = await page.request.put(`/api/accounts/${id}`, { headers: csrf, data: { ...account, enabled: false } })
  if (!response.ok()) throw new Error(`Pausing the account failed: ${response.status()}`)
}

export async function deleteAllAccounts(page: Page) {
  const response = await page.request.get('/api/accounts')
  if (!response.ok()) return // not signed in (test failed early)
  for (const account of (await response.json()) as { id: string }[]) {
    await page.request.delete(`/api/accounts/${account.id}`, { headers: csrf })
  }
}
