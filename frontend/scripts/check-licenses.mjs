// License gate for frontend dependencies.
//
// Shipped packages (reachable from "dependencies", i.e. bundled into the app) must use a permissive
// license compatible with distributing an MIT project. Build-only tooling may additionally use MPL-2.0
// (file-level copyleft, used unmodified and never shipped) and a few data licenses.
//
// Usage: node scripts/check-licenses.mjs [--notices <file>]
//   --notices  also write a third-party notices file (license texts of all shipped packages)

import { execFileSync } from 'node:child_process'
import { readFileSync, writeFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import path from 'node:path'

const SHIPPED_ALLOWED = new Set([
  'MIT',
  'ISC',
  'BSD-2-Clause',
  'BSD-3-Clause',
  '0BSD',
  'Apache-2.0',
  'PostgreSQL',
  'Unlicense',
  'CC0-1.0',
  'BlueOak-1.0.0',
  'MIT-0',
])
const BUILD_ONLY_ALLOWED = new Set([...SHIPPED_ALLOWED, 'MPL-2.0', 'CC-BY-3.0', 'CC-BY-4.0', 'Python-2.0'])

const root = process.cwd()
const lockPackages = JSON.parse(readFileSync(path.join(root, 'package-lock.json'), 'utf8')).packages

const require = createRequire(import.meta.url)
const checkerBin = require.resolve('license-checker-rseidelsohn/bin/license-checker-rseidelsohn')
const report = JSON.parse(
  execFileSync(process.execPath, [checkerBin, '--json', '--excludePrivatePackages', '--start', root], {
    encoding: 'utf8',
    maxBuffer: 64 * 1024 * 1024,
  }),
)

/** Evaluates an SPDX-ish expression such as "MIT", "(MIT OR Apache-2.0)" or "MIT AND ISC". */
function isAllowed(expression, allowed) {
  const normalized = expression.replace(/[()]/g, '').replace(/\*$/, '').trim()
  return normalized
    .split(/\s+OR\s+/i)
    .some((alternative) => alternative.split(/\s+AND\s+/i).every((id) => allowed.has(id.trim())))
}

/** Resolves a dependency the way Node does: nearest node_modules folder upwards from the requiring package. */
function resolveLockPath(fromPath, name) {
  let base = fromPath
  for (;;) {
    const candidate = base ? `${base}/node_modules/${name}` : `node_modules/${name}`
    if (lockPackages[candidate]) return candidate
    if (!base) return null
    const parent = base.lastIndexOf('/node_modules/')
    base = parent === -1 ? '' : base.slice(0, parent)
  }
}

/**
 * Lock paths of every package reachable from the production dependencies, i.e. what ends up in the bundle.
 * (npm's own dev/devOptional flags are not reliable for this, e.g. for optional native binaries.)
 */
function collectShipped() {
  const shipped = new Set()
  const queue = Object.keys(lockPackages[''].dependencies ?? {}).map((name) => resolveLockPath('', name))
  while (queue.length > 0) {
    const current = queue.pop()
    if (!current || shipped.has(current)) continue
    shipped.add(current)
    const entry = lockPackages[current]
    const optionalPeers = entry.peerDependenciesMeta ?? {}
    const names = [
      ...Object.keys(entry.dependencies ?? {}),
      ...Object.keys(entry.optionalDependencies ?? {}),
      ...Object.keys(entry.peerDependencies ?? {}).filter((name) => !optionalPeers[name]?.optional),
    ]
    for (const name of names) queue.push(resolveLockPath(current, name))
  }
  return shipped
}

const shippedPaths = collectShipped()
const toLockPath = (packagePath) => path.relative(root, packagePath).split(path.sep).join('/')

const violations = []
const shipped = []

for (const [id, info] of Object.entries(report)) {
  const lockPath = toLockPath(info.path)
  if (!lockPackages[lockPath] || lockPath === '') continue // the root project itself
  const isShipped = shippedPaths.has(lockPath)
  const licenses = [info.licenses ?? 'UNKNOWN'].flat()
  const allowed = isShipped ? SHIPPED_ALLOWED : BUILD_ONLY_ALLOWED
  if (!licenses.every((license) => isAllowed(license, allowed))) {
    violations.push(`${isShipped ? 'shipped   ' : 'build-only'}  ${id}  →  ${licenses.join(', ')}`)
  }
  if (isShipped) shipped.push({ id, info, licenses })
}

const noticesIndex = process.argv.indexOf('--notices')
if (noticesIndex !== -1) {
  const target = process.argv[noticesIndex + 1]
  const sections = shipped
    .sort((a, b) => a.id.localeCompare(b.id))
    .map(({ id, info, licenses }) => {
      const text = info.licenseFile ? readFileSync(info.licenseFile, 'utf8').trim() : '(no license file provided)'
      return `## ${id}\nLicense: ${licenses.join(', ')}\n${info.repository ? `Source: ${info.repository}\n` : ''}\n${text}\n`
    })
  writeFileSync(target, `# Third-party notices (frontend)\n\n${sections.join('\n---\n\n')}`)
  console.log(`Wrote notices for ${shipped.length} shipped packages to ${target}`)
}

if (violations.length > 0) {
  console.error('License check failed. Packages with licenses outside the allowlist:\n')
  for (const line of violations) console.error(`  ${line}`)
  process.exit(1)
}

console.log(`License check passed (${shipped.length} shipped, ${Object.keys(report).length - shipped.length} build-only packages).`)
