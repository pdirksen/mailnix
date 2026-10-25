// Derives every icon from the one logo source, src/assets/brand/logo-mark.svg (run after changing it):
//   public/favicon.svg, PNG icons for home screens and the web manifest, the Windows icon of mailnix.exe and the
//   graphics in docs/brand. Build-time only; renders with Playwright's Chromium (a dev dependency).
//   npm run brand:icons
import { chromium } from '@playwright/test'
import { copyFileSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const frontend = join(dirname(fileURLToPath(import.meta.url)), '..')
const root = join(frontend, '..')
const source = join(frontend, 'src/assets/brand/logo-mark.svg')
const mark = readFileSync(source, 'utf8')

const INK = '#171717'
const WHITE = '#fafafa'

/** The tile without rounded corners: iOS and Android round the icon themselves. */
const fullBleed = mark.replace('rx="7"', 'rx="0"')

/** Android "maskable": full-bleed tile, the symbol within the inner 80 % safe zone. */
const maskable = fullBleed.replace(/(<rect width="32" height="32" rx="0" fill="[^"]+"\/>)([\s\S]*)(<\/svg>)/, (_, tile, symbol, end) =>
  `${tile}<g transform="translate(16 16) scale(0.8) translate(-16 -16)">${symbol}</g>${end}`)

/** Mark plus the wordmark, for the README and documents; on dark backgrounds the tile gets a faint ring. */
function lockup(textColor, ring = false) {
  const inner = mark.replace(/<svg[^>]*>/, '').replace('</svg>', '').replace(/<title>.*<\/title>/, '')
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 184 48" width="184" height="48">
  <title>mailnix</title>
  <svg x="0" y="0" width="48" height="48" viewBox="0 0 32 32">${inner}${ring ? '<rect x=".25" y=".25" width="31.5" height="31.5" rx="6.8" fill="none" stroke="#fff" stroke-opacity=".15" stroke-width=".5"/>' : ''}</svg>
  <text x="60" y="33" fill="${textColor}" font-family="ui-sans-serif, system-ui, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif" font-size="28" font-weight="600" letter-spacing="-0.3">mailnix</text>
</svg>
`
}

const browser = await chromium.launch()
const page = await browser.newPage({ deviceScaleFactor: 1 })

async function png(svg, size) {
  await page.setViewportSize({ width: size, height: size })
  await page.setContent(
    `<body style="margin:0"><img width="${size}" height="${size}" src="data:image/svg+xml;base64,${Buffer.from(svg).toString('base64')}"></body>`,
  )
  await page.locator('img').evaluate((img) => img.decode())
  return page.screenshot({ omitBackground: true, clip: { x: 0, y: 0, width: size, height: size } })
}

/** An .ico with PNG-compressed images (supported since Windows Vista). */
function ico(images) {
  const header = Buffer.alloc(6 + 16 * images.length)
  header.writeUInt16LE(0, 0)
  header.writeUInt16LE(1, 2)
  header.writeUInt16LE(images.length, 4)
  let offset = header.length
  images.forEach(({ size, data }, i) => {
    const entry = 6 + 16 * i
    header.writeUInt8(size >= 256 ? 0 : size, entry)
    header.writeUInt8(size >= 256 ? 0 : size, entry + 1)
    header.writeUInt16LE(1, entry + 4)
    header.writeUInt16LE(32, entry + 6)
    header.writeUInt32LE(data.length, entry + 8)
    header.writeUInt32LE(offset, entry + 12)
    offset += data.length
  })
  return Buffer.concat([header, ...images.map((image) => image.data)])
}

const pub = join(frontend, 'public')
copyFileSync(source, join(pub, 'favicon.svg'))
writeFileSync(join(pub, 'apple-touch-icon.png'), await png(fullBleed, 180))
writeFileSync(join(pub, 'icon-192.png'), await png(mark, 192))
writeFileSync(join(pub, 'icon-512.png'), await png(mark, 512))
writeFileSync(join(pub, 'icon-maskable-512.png'), await png(maskable, 512))
writeFileSync(
  join(pub, 'site.webmanifest'),
  `${JSON.stringify(
    {
      name: 'mailnix',
      short_name: 'mailnix',
      start_url: '/',
      display: 'standalone',
      background_color: INK,
      theme_color: INK,
      icons: [
        { src: '/icon-192.png', sizes: '192x192', type: 'image/png' },
        { src: '/icon-512.png', sizes: '512x512', type: 'image/png' },
        { src: '/icon-maskable-512.png', sizes: '512x512', type: 'image/png', purpose: 'maskable' },
      ],
    },
    null,
    2,
  )}\n`,
)

const icoImages = []
for (const size of [16, 24, 32, 48, 64, 256]) icoImages.push({ size, data: await png(mark, size) })
writeFileSync(join(root, 'backend/src/Mailnix.Api/mailnix.ico'), ico(icoImages))

const docs = join(root, 'docs/brand')
mkdirSync(docs, { recursive: true })
copyFileSync(source, join(docs, 'logo-mark.svg'))
writeFileSync(join(docs, 'logo-lockup-light.svg'), lockup(INK))
writeFileSync(join(docs, 'logo-lockup-dark.svg'), lockup(WHITE, true))
writeFileSync(join(docs, 'logo-mark-512.png'), await png(mark, 512))

await browser.close()
console.log('Icons written: frontend/public, backend/src/Mailnix.Api/mailnix.ico, docs/brand')
