import { defineConfig, devices } from '@playwright/test'

/**
 * End-to-end tests against a running mailnix instance (backend serving the built SPA).
 * Set MAILNIX_E2E_URL to target another instance.
 */
export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? 'github' : 'list',
  use: {
    baseURL: process.env.MAILNIX_E2E_URL ?? 'http://localhost:5080',
    trace: 'retain-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
})
