import { api, CSRF_HEADER, type Schemas, unwrap } from '@/api/client'
import { defineStore } from 'pinia'
import { ref } from 'vue'

export type ListSummary = Schemas['ListSummaryResponse']
export type NamedList = Schemas['ListResponse']
export type ListEntry = Schemas['ListEntryDto']
export type ListKind = Schemas['NamedListKind']
export type ListImportResult = Schemas['ListImportResponse']

export const LIST_KINDS: ListKind[] = ['Address', 'Domain', 'Pattern']

export const useListsStore = defineStore('lists', () => {
  const lists = ref<ListSummary[]>([])
  const loaded = ref(false)

  async function fetchAll() {
    lists.value = unwrap(await api.GET('/api/lists'))
    loaded.value = true
  }

  async function fetchOne(id: string) {
    return unwrap(await api.GET('/api/lists/{id}', { params: { path: { id } } }))
  }

  async function create(input: Schemas['CreateListRequest']) {
    const list = unwrap(await api.POST('/api/lists', { body: input }))
    await fetchAll()
    return list
  }

  async function update(id: string, input: Schemas['UpdateListRequest']) {
    const list = unwrap(await api.PUT('/api/lists/{id}', { params: { path: { id } }, body: input }))
    await fetchAll()
    return list
  }

  async function remove(id: string) {
    unwrap(await api.DELETE('/api/lists/{id}', { params: { path: { id } } }))
    lists.value = lists.value.filter((l) => l.id !== id)
  }

  /** Imports CSV text (value,note per line); `replace` drops entries not in the file. */
  async function importCsv(id: string, csv: string, replace = false) {
    const result = unwrap(
      await api.POST('/api/lists/{id}/import', {
        params: { path: { id }, query: { replace } },
        body: csv as never,
        bodySerializer: (body: unknown) => body as string,
        headers: { ...CSRF_HEADER, 'Content-Type': 'text/csv' },
      }),
    )
    await fetchAll()
    return result
  }

  function exportUrl(id: string) {
    return `/api/lists/${id}/export`
  }

  return { lists, loaded, fetchAll, fetchOne, create, update, remove, importCsv, exportUrl }
})
