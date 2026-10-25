import type { Page } from '@playwright/test'

/** Deterministic, realistic stats for UI tests and screenshots (the API answers are simulated). */

function days(count: number) {
  const today = new Date()
  return Array.from({ length: count }, (_, i) => {
    const d = new Date(today)
    d.setDate(d.getDate() - (count - 1 - i))
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
  })
}

/** A smooth, repeatable "random" series. */
function wave(n: number, base: number, amplitude: number, phase: number) {
  return Array.from({ length: n }, (_, i) =>
    Math.max(0, Math.round(base + amplitude * Math.sin(i / 3 + phase) + ((i * 7 + phase * 13) % 5) - 2)),
  )
}

export const accounts = [
  {
    id: '00000000-0000-0000-0000-0000000000a1',
    name: 'Work',
    provider: 'Imap',
    enabled: true,
    status: 'Ok',
    lastErrorCode: null,
    lastCheckedAt: new Date().toISOString(),
    processedToday: 36,
    actionsToday: 24,
  },
  {
    id: '00000000-0000-0000-0000-0000000000a2',
    name: 'Private Gmail',
    provider: 'Google',
    enabled: true,
    status: 'Ok',
    lastErrorCode: null,
    lastCheckedAt: new Date().toISOString(),
    processedToday: 12,
    actionsToday: 7,
  },
]

export function overview() {
  const buckets = days(30)
  return {
    today: buckets[buckets.length - 1],
    todayKpis: { processed: 48, actions: 31, moved: 22, trashed: 4, flagged: 5, failed: 0 },
    yesterdayKpis: { processed: 52, actions: 27, moved: 19, trashed: 3, flagged: 5, failed: 1 },
    pendingConfirmations: 1,
    accountsWithErrors: 0,
    unread: 133,
    accounts,
    activity: {
      buckets,
      series: [
        { key: 'MoveTo', name: null, values: wave(30, 14, 5, 0) },
        { key: 'Archive', name: null, values: wave(30, 3, 2, 1) },
        { key: 'Delete', name: null, values: wave(30, 4, 2, 2) },
        { key: 'Flag', name: null, values: wave(30, 5, 3, 3) },
        { key: 'MarkRead', name: null, values: wave(30, 6, 3, 4) },
      ],
    },
    topRules: [
      {
        ruleId: '00000000-0000-0000-0000-0000000000r1',
        name: 'Newsletters to folder',
        accountId: accounts[0]!.id,
        count: 96,
      },
      {
        ruleId: '00000000-0000-0000-0000-0000000000r2',
        name: 'Shop orders by plus-tag',
        accountId: accounts[0]!.id,
        count: 41,
      },
      { ruleId: '00000000-0000-0000-0000-0000000000r3', name: 'Flag invoices', accountId: accounts[1]!.id, count: 23 },
      {
        ruleId: '00000000-0000-0000-0000-0000000000r4',
        name: 'Auto-replies away',
        accountId: accounts[0]!.id,
        count: 9,
      },
    ],
    hints: [
      {
        kind: 'DryRunReady',
        ruleId: '00000000-0000-0000-0000-0000000000r5',
        ruleName: 'Old newsletters to Trash',
        count: 17,
      },
    ],
  }
}

export function actions() {
  const subjects = [
    'Weekly deals: 30% off',
    'Your invoice 2026-1042',
    'Re: Project kickoff',
    'Your order has shipped',
    'Out of office: back Monday',
    'Newsletter October',
  ]
  const types = ['MoveTo', 'Flag', 'MoveTo', 'MoveTo', 'Delete', 'Archive']
  return {
    items: subjects.map((subject, i) => ({
      id: `00000000-0000-0000-0000-00000000000${i}`,
      runId: '00000000-0000-0000-0000-0000000000f1',
      accountId: accounts[0]!.id,
      accountName: 'Work',
      ruleId: '00000000-0000-0000-0000-0000000000r1',
      ruleName: 'Newsletters to folder',
      action: types[i],
      keyword: null,
      sourceFolder: 'INBOX',
      targetFolder:
        types[i] === 'MoveTo'
          ? 'INBOX/Newsletters'
          : types[i] === 'Delete'
            ? 'INBOX/Trash'
            : types[i] === 'Archive'
              ? 'Archive'
              : null,
      from: [
        'news@shop.example',
        'billing@vendor.example',
        'alice@example.org',
        'orders@shop.example',
        'bob@example.org',
        'news@brand.example',
      ][i],
      subject,
      dryRun: false,
      success: true,
      error: null,
      createdAt: new Date(Date.now() - i * 37 * 60_000).toISOString(),
      undoneAt: null,
      canUndo: true,
    })),
    nextBefore: null,
  }
}

function timeSeries(url: URL) {
  const buckets = days(30)
  const metric = url.searchParams.get('metric')
  const groupBy = url.searchParams.get('groupBy')
  if (groupBy === 'action') return overview().activity
  const values =
    metric === 'processed' ? wave(30, 45, 10, 0.5) : metric === 'Matched' ? wave(30, 8, 4, 1) : wave(30, 28, 8, 1.5)
  return { buckets, series: [{ key: metric ?? 'actions', name: null, values }] }
}

const statsAnswers: Record<string, (url: URL) => unknown> = {
  '/api/stats/timeseries': timeSeries,
  '/api/stats/rules': () => ({
    from: days(30)[0],
    to: days(30)[29],
    rules: overview()
      .topRules.map((r, i) => ({
        ruleId: r.ruleId,
        name: r.name,
        accountId: r.accountId,
        accountName: i === 2 ? 'Private Gmail' : 'Work',
        enabled: true,
        dryRun: false,
        matched: r.count * 4,
        actions: r.count * 5,
        failed: i === 1 ? 2 : 0,
        undone: i === 0 ? 3 : 0,
        dryRunActions: 0,
        lastMatch: days(30)[29 - i],
        matchesPerDay: (r.count * 4) / 30,
      }))
      .concat([
        {
          ruleId: '00000000-0000-0000-0000-0000000000r9',
          name: 'Old project mails',
          accountId: accounts[0]!.id,
          accountName: 'Work',
          enabled: true,
          dryRun: false,
          matched: 0,
          actions: 0,
          failed: 0,
          undone: 0,
          dryRunActions: 0,
          lastMatch: null as unknown as string,
          matchesPerDay: 0,
        },
      ]),
  }),
  '/api/stats/senders': () => {
    const rows = [
      ['shop.example', 420, 380, 400, 0],
      ['github.com', 260, 40, 210, 0],
      ['example.org', 190, 12, 0, 0],
      ['bank.example', 75, 75, 10, 0],
      ['brand.example', 64, 60, 64, 3],
      ['suspicious.example', 22, 22, 0, 22],
    ].map(([domain, processed, actioned, bulk, authFailed]) => ({ domain, processed, actioned, bulk, authFailed }))
    return {
      totals: { domain: '', processed: 1430, actioned: 620, bulk: 700, authFailed: 25 },
      topProcessed: rows,
      topActioned: [...rows].sort((a, b) => (b.actioned as number) - (a.actioned as number)),
      volume: { buckets: days(30), series: [] },
    }
  },
  '/api/stats/folders': () => ({
    date: days(90)[89],
    folders: [
      ['INBOX', 1840, 120, -310],
      ['INBOX/Newsletters', 2210, 980, 640],
      ['Archive', 15400, 0, 220],
      ['INBOX/Trash', 310, 12, 40],
    ].map(([folder, messages, unread, change]) => ({
      accountId: accounts[0]!.id,
      accountName: 'Work',
      folder,
      messages,
      unread,
      change,
    })),
    growth: {
      buckets: days(90),
      series: [
        { key: 'messages', name: null, values: wave(90, 19000, 300, 0).map((v, i) => v + i * 10) },
        { key: 'unread', name: null, values: wave(90, 1100, 120, 1) },
      ],
    },
  }),
  '/api/stats/security': () => ({
    totals: { processed: 1430, spfFailed: 18, dkimFailed: 9, dmarcFailed: 25, spoofing: 4, highSpam: 31 },
    series: {
      buckets: days(30),
      series: [
        { key: 'spf', name: null, values: wave(30, 1, 1, 0) },
        { key: 'dkim', name: null, values: wave(30, 0, 1, 1) },
        { key: 'dmarc', name: null, values: wave(30, 1, 1.5, 2) },
      ],
    },
    topFailingDomains: [
      { domain: 'suspicious.example', processed: 22, actioned: 22, bulk: 0, authFailed: 22 },
      { domain: 'brand.example', processed: 64, actioned: 60, bulk: 64, authFailed: 3 },
    ],
  }),
  '/api/stats/reliability': () => ({
    runsByState: [
      { state: 'Completed', count: 412 },
      { state: 'CompletedWithErrors', count: 3 },
      { state: 'AwaitingConfirmation', count: 1 },
    ],
    byTrigger: [
      { trigger: 'NewMail', runs: 380, failed: 2, averageSeconds: 0.8 },
      { trigger: 'Scheduled', runs: 30, failed: 1, averageSeconds: 6.4 },
      { trigger: 'Manual', runs: 5, failed: 0, averageSeconds: 3.1 },
    ],
    failedActions: {
      buckets: days(30),
      series: [{ key: 'Failed', name: null, values: days(30).map((_, i) => (i === 12 ? 2 : i === 25 ? 1 : 0)) }],
    },
    pendingNow: 1,
    accounts,
  }),
}

/** Answers the stats, activity and account calls with the fixture data. */
export async function mockStats(page: Page) {
  await page.route('**/api/stats/overview*', (route) => route.fulfill({ json: overview() }))
  await page.route('**/api/activity/actions*', (route) => route.fulfill({ json: actions() }))
  for (const [path, answer] of Object.entries(statsAnswers)) {
    await page.route(`**${path}*`, (route) => route.fulfill({ json: answer(new URL(route.request().url())) }))
  }
}
