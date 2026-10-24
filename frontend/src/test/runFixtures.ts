import type { RunSummary } from '@/stores/runs'

export function testRun(overrides: Partial<RunSummary> = {}): RunSummary {
  return {
    id: 'run-1',
    accountId: 'account-1',
    accountName: 'Work',
    trigger: 'Manual',
    state: 'Completed',
    folder: 'INBOX',
    searchFilter: null,
    messagesExamined: 10,
    messagesMatched: 3,
    plannedActions: 4,
    executedActions: 4,
    failedActions: 0,
    error: null,
    createdAt: '2026-10-24T10:00:00Z',
    startedAt: '2026-10-24T10:00:01Z',
    finishedAt: '2026-10-24T10:00:02Z',
    expiresAt: null,
    undoneByRunId: null,
    undoRunId: null,
    undoRuleId: null,
    canUndo: true,
    ...overrides,
  }
}
