// Screenshots of the app for the front page (docs/frontpage/assets/), taken from the demo instance with made-up mail:
//   1. npm run build, then copy dist/ to backend/src/Mailnix.Api/wwwroot (as for the e2e tests)
//   2. in backend/: dotnet test --project tests/Mailnix.Integration.Tests -- --filter-method "*Run_the_demo_instance" --explicit only
//   3. npm run frontpage:screenshots (waits until the demo is ready)
// Build-time only; uses Playwright's Chromium (a dev dependency).
import { chromium } from '@playwright/test'
import { existsSync, mkdirSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const frontend = join(dirname(fileURLToPath(import.meta.url)), '..')
const output = join(frontend, '..', 'docs', 'frontpage', 'assets')
const base = process.env.MAILNIX_DEMO_URL ?? 'http://localhost:5090'
// Must match DemoHost.cs.
const ready = join(tmpdir(), 'mailnix-demo.ready')
const credentials = { email: 'alex@example.com', password: 'demo password 123', rememberMe: false }

for (let waited = 0; !existsSync(ready); waited++) {
  if (waited === 0) console.log('Waiting for the demo instance (DemoHost.cs) ...')
  if (waited > 360) throw new Error(`The demo instance did not get ready (${ready} missing).`)
  await new Promise((resolve) => setTimeout(resolve, 500))
}

mkdirSync(output, { recursive: true })
const browser = await chromium.launch()

async function shoot(page, name, theme) {
  const file = join(output, `${name}-${theme}.jpg`)
  await page.screenshot({ path: file, type: 'jpeg', quality: 86 })
  console.log(`  ${file}`)
}

for (const theme of ['light', 'dark']) {
  const context = await browser.newContext({
    baseURL: base,
    viewport: { width: 1440, height: 900 },
    deviceScaleFactor: 2,
    colorScheme: theme,
    locale: 'en-US',
    timezoneId: 'Europe/Berlin',
  })
  const page = await context.newPage()
  const login = await page.request.post('/api/auth/login', { headers: { 'X-Requested-With': 'mailnix' }, data: credentials })
  if (!login.ok()) throw new Error(`Signing in to the demo failed: ${login.status()}`)

  // The rule editor: the newsletter rule previewed on the archive, one message explained.
  await page.goto('/rules')
  await page.getByRole('link', { name: 'Newsletters', exact: true }).click()
  await page.getByTestId('preview-panel').waitFor()
  await page.locator('#preview-folder').click()
  await page.getByRole('option', { name: 'Archive', exact: true }).click()
  await page.getByTestId('run-preview').click()
  await page.getByTestId('preview-messages').waitFor()
  await page.getByTestId('preview-messages').locator('li button').first().click()
  await page.getByTestId('preview-details').waitFor()
  await page.mouse.move(0, 0)
  await page.waitForTimeout(400)
  await shoot(page, 'rule-editor', theme)

  // The dashboard, with its charts drawn.
  await page.goto('/')
  await page.locator('canvas').first().waitFor()
  await page.waitForTimeout(1500)
  await shoot(page, 'dashboard', theme)

  // Activity: runs with undo, the first "Run now" opened to its actions.
  await page.goto('/activity')
  const runs = page.getByTestId('run-list').locator('> li')
  await runs.last().getByRole('button', { name: 'Details' }).click()
  await runs.last().getByRole('table').waitFor()
  await page.mouse.move(0, 0)
  await page.waitForTimeout(600)
  await shoot(page, 'activity', theme)

  await context.close()
}

await browser.close()
