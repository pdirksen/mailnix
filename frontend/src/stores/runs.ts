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

  async function fetchRecent(limit = 100) {
    runs.value = unwrap(await api.GET('/api/runs', { params: { query: { limit } } }))
    loaded.value = true
  }

  async function fetchDetail(id: string) {
    return unwrap(await api.GET('/api/runs/{id}', { params: { path: { id } } }))
  }

  function upsert(run: RunSummary) {
    const index = runs.value.findIndex((r) => r.id === run.id)
    if (index === -1) runs.value.unshift(run)
    else runs.value[index] = run
  }

  /** "Run now": applies the account's rules (or the given ones) to the newest messages of a folder. */
  async function start(accountId: string, options: { folder?: string; limit: number; ruleIds?: string[] }) {
    const run = unwrap(
      await api.POST('/api/accounts/{id}/runs', {
        params: { path: { id: accountId } },
        body: { folder: options.folder ?? null, limit: options.limit, ruleIds: options.ruleIds ?? null },
      }),
    )
    upsert(run)
    return run
  }

  async function confirm(id: string) {
    upsert(unwrap(await api.POST('/api/runs/{id}/confirm', { params: { path: { id } } })))
  }

  async function cancel(id: string) {
    upsert(unwrap(await api.POST('/api/runs/{id}/cancel', { params: { path: { id } } })))
  }

  async function undo(id: string) {
    const run = unwrap(await api.POST('/api/runs/{id}/undo', { params: { path: { id } } }))
    upsert(run)
    return run
  }

  async function undoRule(ruleId: string, count: number) {
    const run = unwrap(await api.POST('/api/rules/{id}/undo', { params: { path: { id: ruleId } }, body: { count } }))
    upsert(run)
    return run
  }

  return { runs, loaded, pending, fetchRecent, fetchDetail, upsert, start, confirm, cancel, undo, undoRule }
})
