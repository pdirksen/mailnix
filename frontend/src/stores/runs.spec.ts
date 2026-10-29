import { apiMock, apiResult } from '@/test/apiMock'
import { testRun } from '@/test/runFixtures'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { useRunsStore } from './runs'

vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  api: (await import('@/test/apiMock')).apiMock,
}))

describe('runs store', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.resetAllMocks()
  })

  it('lists runs waiting for confirmation', async () => {
    apiMock.GET.mockResolvedValue(
      apiResult(200, [testRun(), testRun({ id: 'run-2', state: 'AwaitingConfirmation', canUndo: false })]),
    )
    const runs = useRunsStore()

    await runs.fetchRecent()

    expect(runs.loaded).toBe(true)
    expect(runs.pending.map((r) => r.id)).toEqual(['run-2'])
  })

  it('keeps the newer list when an older request answers last', async () => {
    const runs = useRunsStore()
    let answerOld!: (value: unknown) => void
    apiMock.GET.mockReturnValueOnce(new Promise((resolve) => (answerOld = resolve)))
    apiMock.GET.mockResolvedValueOnce(apiResult(200, [testRun({ state: 'Completed' })]))

    // The page's first load is still on its way when a live update triggers another one.
    const old = runs.fetchRecent(200)
    await runs.fetchRecent()
    answerOld(apiResult(200, [testRun({ state: 'Running' })]))
    await old

    expect(runs.runs[0]!.state).toBe('Completed')
  })

  it('does not let a late answer to "run now" undo a newer list', async () => {
    const runs = useRunsStore()
    let answerStart!: (value: unknown) => void
    apiMock.POST.mockReturnValueOnce(new Promise((resolve) => (answerStart = resolve)))
    apiMock.GET.mockResolvedValueOnce(apiResult(200, [testRun({ id: 'run-2', state: 'Completed' })]))

    const started = runs.start('account-1', { limit: 50 })
    await runs.fetchRecent()
    answerStart(apiResult(202, testRun({ id: 'run-2', state: 'Queued' })))
    await started

    expect(runs.runs.map((r) => r.state)).toEqual(['Completed'])
  })

  it('adds a started run in front and replaces it when it changes', async () => {
    const runs = useRunsStore()
    runs.runs = [testRun()]
    apiMock.POST.mockResolvedValueOnce(apiResult(202, testRun({ id: 'run-2', state: 'Queued' })))

    await runs.start('account-1', { folder: 'INBOX', limit: 50, ruleIds: ['rule-1'] })

    expect(apiMock.POST).toHaveBeenCalledWith('/api/accounts/{id}/runs', {
      params: { path: { id: 'account-1' } },
      body: { folder: 'INBOX', limit: 50, ruleIds: ['rule-1'] },
    })
    expect(runs.runs.map((r) => r.id)).toEqual(['run-2', 'run-1'])

    runs.upsert(testRun({ id: 'run-2', state: 'Completed' }))
    expect(runs.runs).toHaveLength(2)
    expect(runs.runs[0]!.state).toBe('Completed')
  })

  it('confirming a run removes it from the pending list', async () => {
    const runs = useRunsStore()
    runs.runs = [testRun({ state: 'AwaitingConfirmation' })]
    apiMock.POST.mockResolvedValueOnce(apiResult(200, testRun({ state: 'Queued' })))

    await runs.confirm('run-1')

    expect(runs.pending).toHaveLength(0)
  })

  it('undoing a rule sends the number of actions', async () => {
    const runs = useRunsStore()
    apiMock.POST.mockResolvedValueOnce(apiResult(202, testRun({ id: 'undo-1', trigger: 'Undo', state: 'Queued' })))

    const run = await runs.undoRule('rule-1', 5)

    expect(apiMock.POST).toHaveBeenCalledWith('/api/rules/{id}/undo', { params: { path: { id: 'rule-1' } }, body: { count: 5 } })
    expect(run.trigger).toBe('Undo')
  })
})
