import { api, type Schemas, unwrap } from '@/api/client'
import { defineStore } from 'pinia'
import { ref } from 'vue'

export type ApiToken = Schemas['ApiTokenResponse']
export type ApiScope = Schemas['ApiScope']
export type CreatedApiToken = Schemas['CreatedApiTokenResponse']
export type ConnectRequest = Schemas['ConnectRequest']

/** Access for AI assistants over MCP: personal tokens and apps connected through OAuth (Settings → AI assistants). */
export const useAssistantsStore = defineStore('assistants', () => {
  const tokens = ref<ApiToken[]>([])
  const mcpUrl = ref('')
  const loaded = ref(false)

  async function load() {
    const result = unwrap(await api.GET('/api/tokens'))
    tokens.value = result.tokens
    mcpUrl.value = result.mcpUrl
    loaded.value = true
  }

  async function create(name: string, scope: ApiScope, expiresInDays: number | null) {
    const created = unwrap(await api.POST('/api/tokens', { body: { name, scope, expiresInDays } }))
    tokens.value = [created.token, ...tokens.value]
    return created
  }

  async function revoke(id: string) {
    unwrap(await api.DELETE('/api/tokens/{id}', { params: { path: { id } } }))
    tokens.value = tokens.value.filter((t) => t.id !== id)
  }

  /** The app asking for access, for the consent page; throws ApiError "ConnectInvalidApp" for unknown apps. */
  async function connectingApp(clientId: string, redirectUri: string, scope?: string) {
    return unwrap(await api.GET('/api/connect/app', { params: { query: { clientId, redirectUri, scope } } }))
  }

  /** Records the decision; returns where to send the browser (back to the app). */
  async function decide(request: ConnectRequest) {
    return unwrap(await api.POST('/api/connect', { body: request })).redirectUrl
  }

  return { tokens, mcpUrl, loaded, load, create, revoke, connectingApp, decide }
})
