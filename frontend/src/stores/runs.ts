import { api, type Schemas, unwrap } from '@/api/client'
import { defineStore } from 'pinia'
import { computed, ref } from 'vue'

export type RunSummary = Schemas['RunSummary']
export type RunDetail = Schemas['RunDetail']
export type RunState = Schemas['RunState']

export const useRunsStore = defineStore('runs', () => {
  const runs = ref<RunSummary[]>([])
  const loaded = ref(false)

  const pending = computed(() => runs.value.filter((r) => r.state === 'AwaitingConfirmation'))

  // Answers can arrive out of order, e.g. a slow first load of the page after a live update's refresh. A list or a
  // single run is only applied when no list requested after it has been applied, so an older state never wins.
  let requested = 0
  let applied = 0

  async function fetchRecent(limit = 100) {
    const request = ++requested
    const result = unwrap(await api.GET('/api/runs', { params: { query: { limit } } }))
    if (request < applied) return
    applied = request
    runs.value = result
    loaded.value = true
  }

  async function fetchDetail(id: string) {
    return unwrap(await api.GET('/api/runs/{id}', { params: { path: { id } } }))
  }

  /** `request`: the number of the request that answered with this run (see `send`); without it the run always applies. */
  function upsert(run: RunSummary, request?: number) {
    if (request !== undefined && request < applied) return
    const index = runs.value.findIndex((r) => r.id === run.id)
    if (index === -1) runs.value.unshift(run)
    else runs.value[index] = run
  }

  /** Sends a request that answers with a run, and records the run. */
  async function send(call: () => Promise<RunSummary>) {
    const request = ++requested
    const run = await call()
    upsert(run, request)
    return run
  }

  /** "Run now": applies the account's rules (or the given ones) to the newest messages of a folder. */
  function start(accountId: string, options: { folder?: string; limit: number; ruleIds?: string[] }) {
    return send(async () =>
      unwrap(
        await api.POST('/api/accounts/{id}/runs', {
          params: { path: { id: accountId } },
          body: { folder: options.folder ?? null, limit: options.limit, ruleIds: options.ruleIds ?? null },
        }),
      ),
    )
  }

  async function confirm(id: string) {
    await send(async () => unwrap(await api.POST('/api/runs/{id}/confirm', { params: { path: { id } } })))
  }

  async function cancel(id: string) {
    await send(async () => unwrap(await api.POST('/api/runs/{id}/cancel', { params: { path: { id } } })))
  }

  function undo(id: string) {
    return send(async () => unwrap(await api.POST('/api/runs/{id}/undo', { params: { path: { id } } })))
  }

  function undoRule(ruleId: string, count: number) {
    return send(async () => unwrap(await api.POST('/api/rules/{id}/undo', { params: { path: { id: ruleId } }, body: { count } })))
  }

  return { runs, loaded, pending, fetchRecent, fetchDetail, upsert, start, confirm, cancel, undo, undoRule }
})
