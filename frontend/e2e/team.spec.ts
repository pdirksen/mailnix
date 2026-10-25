import { expect, test } from '@playwright/test'

const password = 'e2e password 123'

test('a founder invites a colleague into their organization', async ({ page, browser }) => {
  const config = await (await page.request.get('/api/auth/config')).json()
  test.skip(!config.multiTenant, 'needs an instance in SaaS mode (Mailnix__Tenancy__Mode=MultiTenant)')

  const stamp = Date.now()
  const organization = `E2E Org ${stamp}`
  await page.goto('/register')
  await expect(page.getByRole('heading', { name: 'Create your account' })).toBeVisible()
  await page.getByLabel('Email').fill(`founder-${stamp}@example.com`)
  await page.getByLabel('Organization').fill(organization)
  await page.getByLabel('Password', { exact: true }).fill(password)
  await page.getByLabel('Confirm password').fill(password)
  await page.getByRole('button', { name: 'Create account' }).click()

  await page.getByRole('link', { name: 'Team' }).click()
  await expect(page.getByLabel('Name')).toHaveValue(organization)
  const colleague = `colleague-${stamp}@example.com`
  await page.getByLabel('Email address').fill(colleague)
  await page.getByRole('button', { name: 'Create invitation' }).click()
  const link = await page.getByTestId('invitation-link').getByRole('textbox').inputValue()
  await expect(page.getByTestId('invitations')).toContainText(colleague)

  // The colleague opens the link in their own browser.
  const other = await browser.newContext()
  const invited = await other.newPage()
  await invited.goto(new URL(link).pathname + new URL(link).search)
  await expect(invited.getByRole('heading', { name: `Join ${organization}` })).toBeVisible()
  await expect(invited.getByLabel('Email')).toHaveValue(colleague)
  await expect(invited.getByLabel('Organization')).toHaveCount(0)
  await invited.getByLabel('Password', { exact: true }).fill(password)
  await invited.getByLabel('Confirm password').fill(password)
  await invited.getByRole('button', { name: 'Create account' }).click()
  await expect(invited.getByRole('heading', { name: /Welcome/ })).toBeVisible()
  await expect(invited.getByRole('link', { name: 'Team' })).toHaveCount(0)
  await other.close()

  await page.reload()
  await expect(page.getByTestId(`member-${colleague}`)).toBeVisible()
  await expect(page.getByTestId('invitations')).toHaveCount(0)

  // Removing the colleague again keeps the instance tidy.
  await page.getByRole('button', { name: `Remove ${colleague}` }).click()
  await page.getByRole('alertdialog').getByRole('button', { name: 'Remove' }).click()
  await expect(page.getByTestId(`member-${colleague}`)).toHaveCount(0)
})
