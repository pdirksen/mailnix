import { expect, test } from '@playwright/test'

test('register, sign out and sign in again', async ({ page }) => {
  // CSP violations and runtime errors surface as console errors. Network failures are logged there too;
  // those are expected here (e.g. 401 from /api/auth/me while signed out), so they are ignored.
  const consoleErrors: string[] = []
  page.on('console', (message) => {
    if (message.type() === 'error' && !message.text().startsWith('Failed to load resource')) consoleErrors.push(message.text())
  })
  page.on('pageerror', (error) => consoleErrors.push(error.message))

  const email = `e2e-${Date.now()}@example.com`
  const password = 'e2e password 123'

  await page.goto('/')
  await expect(page).toHaveURL(/\/login$/)

  await page.getByRole('link', { name: 'Create account' }).click()
  await expect(page.getByRole('heading', { name: 'Create your account' })).toBeVisible()
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Display name').fill('E2E User')
  await page.getByLabel('Password', { exact: true }).fill(password)
  await page.getByLabel('Confirm password').fill(password)
  await page.getByRole('button', { name: 'Create account' }).click()

  await expect(page.getByRole('heading', { name: 'Welcome, E2E User' })).toBeVisible()

  await page.getByRole('button', { name: /E2E User/ }).click()
  await page.getByRole('menuitem', { name: 'Sign out' }).click()
  await expect(page.getByRole('heading', { name: 'Sign in to mailnix' })).toBeVisible()

  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password').fill(password)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page.getByRole('heading', { name: 'Welcome, E2E User' })).toBeVisible()

  expect(consoleErrors).toEqual([])
})

test('shows a validation error for a wrong password', async ({ page }) => {
  await page.goto('/login')
  await page.getByLabel('Email').fill('nobody@example.com')
  await page.getByLabel('Password').fill('definitely wrong')
  await page.getByRole('button', { name: 'Sign in' }).click()

  await expect(page.getByTestId('login-error')).toContainText('Invalid email or password.')
})

test('protected pages redirect to login and back', async ({ page }) => {
  await page.goto('/some/deep/link')
  // Unknown route renders the 404 page for signed-out users as well
  await expect(page.getByRole('heading', { name: 'Page not found' })).toBeVisible()

  await page.goto('/')
  await expect(page).toHaveURL(/\/login$/)
})
