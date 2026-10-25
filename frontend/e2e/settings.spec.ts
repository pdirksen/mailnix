import { expect, type Page, test } from '@playwright/test'
import { createHmac } from 'node:crypto'

const password = 'e2e password 123'

/** What an authenticator app shows for a base32 key right now (RFC 6238, 30 s, 6 digits). */
function totp(base32Key: string, at = Date.now()): string {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'
  let bits = ''
  for (const char of base32Key.replace(/\s/g, '').toUpperCase()) bits += alphabet.indexOf(char).toString(2).padStart(5, '0')
  const key = Buffer.from(bits.match(/.{8}/g)!.map((byte) => parseInt(byte, 2)))
  const counter = Buffer.alloc(8)
  counter.writeBigUInt64BE(BigInt(Math.floor(at / 30_000)))
  const hash = createHmac('sha1', key).update(counter).digest()
  const offset = hash[hash.length - 1]! & 0x0f
  return String((hash.readUInt32BE(offset) & 0x7fffffff) % 1_000_000).padStart(6, '0')
}

async function register(page: Page, email: string) {
  await page.goto('/register')
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password', { exact: true }).fill(password)
  await page.getByLabel('Confirm password').fill(password)
  await page.getByRole('button', { name: 'Create account' }).click()
  await expect(page.getByRole('heading', { name: /Welcome/ })).toBeVisible()
}

test.use({ timezoneId: 'Europe/Berlin' })

test('profile, time zone and own addresses', async ({ page }) => {
  const email = `settings-${Date.now()}@example.com`
  await register(page, email)

  await page.getByRole('link', { name: 'Settings' }).click()
  await page.getByLabel('Display name').fill('Settings Tester')
  await page.getByTestId('save-profile').click()
  await expect(page.getByTestId('profile-name')).toHaveText('Settings Tester')
  await expect(page.getByRole('button', { name: /Settings Tester/ })).toBeVisible()

  // Signed up in a Berlin browser: the profile starts there.
  await page.getByTestId('settings-nav-preferences').click()
  await expect(page.getByTestId('time-zone-input')).toHaveValue('Berlin (Europe)')
  await page.getByTestId('time-zone-input').click()
  await page.getByTestId('time-zone-input').fill('tokyo')
  await page.getByRole('option', { name: /Tokyo/ }).click()
  await expect(page.getByText('Time zone set to Asia/Tokyo.')).toBeVisible()
  await expect(page.getByTestId('zone-time')).toContainText('GMT+9')
  await expect(page.getByTestId('browser-zone-hint')).toBeVisible()

  await page.getByTestId('settings-nav-addresses').click()
  await page.getByLabel('Add an address').fill('Me@Alias.Example.org')
  await page.getByTestId('add-address').click()
  await expect(page.getByTestId('address-me@alias.example.org')).toBeVisible()

  // Everything is saved on the server.
  await page.reload()
  await expect(page.getByTestId('address-me@alias.example.org')).toBeVisible()
  const profile = await (await page.request.get('/api/profile')).json()
  expect(profile).toMatchObject({ displayName: 'Settings Tester', timeZone: 'Asia/Tokyo', addresses: ['me@alias.example.org'] })
})

test('two-factor sign-in: set up, sign in with a code, turn off', async ({ page }) => {
  const email = `two-factor-${Date.now()}@example.com`
  await register(page, email)

  await page.goto('/settings/security')
  await page.getByTestId('two-factor-setup').click()
  await expect(page.getByRole('img', { name: 'QR code for the authenticator app' })).toBeVisible()
  await page.getByRole('button', { name: 'Cannot scan? Enter the key by hand' }).click()
  const key = (await page.getByTestId('shared-key').textContent())!.trim()
  await page.getByRole('button', { name: 'Next' }).click()
  await page.getByLabel('Code from the app').fill(totp(key))
  await page.getByTestId('two-factor-verify').click()
  await expect(page.getByTestId('recovery-codes').locator('li')).toHaveCount(10)
  await page.getByTestId('two-factor-done').click()
  await expect(page.getByTestId('two-factor-status')).toHaveText('On')

  // Sign out and back in: the password alone is not enough any more.
  await page.getByRole('button', { name: new RegExp(email.slice(0, 12)) }).click()
  await page.getByRole('menuitem', { name: 'Sign out' }).click()
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password').fill(password)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page.getByRole('heading', { name: 'Two-factor sign-in' })).toBeVisible()
  await page.getByLabel('Code from the app').fill(totp(key))
  await page.getByTestId('verify-code').click()
  await expect(page.getByRole('heading', { name: /Welcome/ })).toBeVisible()

  await page.goto('/settings/security')
  await page.getByRole('button', { name: 'Turn off' }).click()
  await page.getByRole('dialog').getByLabel('Current password').fill(password)
  await page.getByTestId('confirm-password-submit').click()
  await expect(page.getByTestId('two-factor-status')).toHaveText('Off')
})

test('deleting the own account', async ({ page }) => {
  const email = `delete-me-${Date.now()}@example.com`
  await register(page, email)

  await page.goto('/settings/security')
  await page.getByTestId('delete-account').click()
  await page.getByLabel('Type DELETE to confirm').fill('DELETE')
  await page.getByRole('dialog').getByLabel('Current password').fill(password)
  await page.getByTestId('confirm-password-submit').click()

  await expect(page.getByRole('heading', { name: 'Sign in to mailnix' })).toBeVisible()
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password').fill(password)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page.getByTestId('login-error')).toContainText('Invalid email or password.')
})
