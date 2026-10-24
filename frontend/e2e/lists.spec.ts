import { expect, test } from '@playwright/test'

test('create a list, edit entries, import and export CSV, delete it', async ({ page }) => {
  await page.goto('/register')
  const password = 'e2e password 123'
  await page.getByLabel('Email').fill(`lists-${Date.now()}@example.com`)
  await page.getByLabel('Password', { exact: true }).fill(password)
  await page.getByLabel('Confirm password').fill(password)
  await page.getByRole('button', { name: 'Create account' }).click()
  await expect(page.getByRole('heading', { name: /Welcome/ })).toBeVisible()

  await page.getByRole('link', { name: 'Lists' }).click()
  await expect(page.getByText('No lists yet')).toBeVisible()

  await page.getByRole('button', { name: 'New list' }).click()
  await page.getByRole('dialog').getByLabel('Name').fill('VIP senders')
  await page.getByRole('dialog').getByRole('radio', { name: /^Addresses/ }).click()
  await page.getByTestId('create-list').click()
  await expect(page.getByRole('heading', { name: 'VIP senders' })).toBeVisible()

  // Paste entries, one of them invalid: the server rejects the save and says why.
  await page.getByLabel('Add entries').fill('Boss@Example.com, my boss\nalice@example.org\nnot an address')
  await page.getByTestId('add-entries').click()
  await page.getByTestId('save-list').click()
  await expect(page.getByTestId('list-save-error')).toContainText('not an address: Not an email address.')

  await page.getByRole('button', { name: 'Remove not an address' }).click()
  await page.getByTestId('save-list').click()
  await expect(page.getByTestId('list-entries')).toContainText('boss@example.com') // normalized by the server
  await expect(page.getByTestId('save-list')).toBeDisabled()

  await page.getByPlaceholder('Search').fill('alice')
  await expect(page.getByTestId('list-entries').getByRole('row')).toHaveCount(2) // header + 1

  // CSV import appends and reports invalid lines.
  await page.getByTestId('import-file').setInputFiles({
    name: 'more.csv',
    mimeType: 'text/csv',
    buffer: Buffer.from('value,note\ncarol@example.net,"Friend, old"\ninvalid line\n'),
  })
  await expect(page.getByText('Line 3:')).toBeVisible()
  await page.getByPlaceholder('Search').fill('')
  await expect(page.getByTestId('list-entries')).toContainText('carol@example.net')

  const download = page.waitForEvent('download')
  await page.getByRole('link', { name: 'Export CSV' }).click()
  const file = await download
  expect(file.suggestedFilename()).toBe('VIP_senders.csv')

  await page.getByRole('link', { name: 'Named lists' }).click()
  await expect(page.getByTestId('list-card-VIP senders')).toContainText('3 entries')
  await page.getByTestId('list-card-VIP senders').click()
  await page.getByRole('button', { name: 'Delete' }).click()
  await page.getByRole('alertdialog').getByRole('button', { name: 'Delete' }).click()
  await expect(page.getByText('No lists yet')).toBeVisible()
})
