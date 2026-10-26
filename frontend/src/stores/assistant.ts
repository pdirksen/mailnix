import { api, type Schemas, unwrap } from '@/api/client'
import type { AssistantSelection, Blueprint } from '@/rules/assistant'
import { defineStore } from 'pinia'
import { ref } from 'vue'

export type Analysis = Schemas['AssistantAnalyzeResponse']
export type ApplyResult = Schemas['AssistantApplyResponse']

export const useAssistantStore = defineStore('assistant', () => {
  const blueprints = ref<Blueprint[]>([])

  async function loadBlueprints() {
    if (blueprints.value.length === 0) {
      blueprints.value = unwrap(await api.GET('/api/assistant/blueprints'))
    }
    return blueprints.value
  }

  async function analyze(accountId: string, selection: AssistantSelection, refresh = false) {
    return unwrap(
      await api.POST('/api/accounts/{id}/assistant/analyze', { params: { path: { id: accountId } }, body: { selection, limit: 300, refresh } }),
    )
  }

  async function apply(accountId: string, options: Omit<Schemas['AssistantApplyRequest'], 'selection'> & { selection: AssistantSelection }) {
    return unwrap(await api.POST('/api/accounts/{id}/assistant/apply', { params: { path: { id: accountId } }, body: options }))
  }

  return { blueprints, loadBlueprints, analyze, apply }
})
