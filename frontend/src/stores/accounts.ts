import { api, type Schemas, unwrap } from '@/api/client'
import { defineStore } from 'pinia'
import { ref } from 'vue'

export type Account = Schemas['AccountResponse']
export type ConnectionTest = Schemas['ConnectionTestResponse']
export type FolderInfo = Schemas['MailFolderInfo']
export type FolderList = Schemas['FolderListResponse']
export type SpecialFolders = Schemas['SpecialFolderMap']
export type ImapSecurity = Schemas['ImapSecurity']
export type ConnectionErrorCode = NonNullable<Schemas['ConnectionErrorCode']>
export type TestConnectionInput = Schemas['TestConnectionRequest']
export type CreateAccountInput = Schemas['CreateAccountRequest']
export type UpdateAccountInput = Schemas['UpdateAccountRequest']
export type MailProvider = Schemas['MailProvider']
export type OAuthProvider = Exclude<MailProvider, 'Imap'>
export type OAuthProviderInfo = Schemas['OAuthProviderInfo']

export const DEFAULT_PORTS: Record<ImapSecurity, number> = { SslOnConnect: 993, StartTls: 143, None: 143 }

export const useAccountsStore = defineStore('accounts', () => {
  const accounts = ref<Account[]>([])
  const loaded = ref(false)

  function upsert(account: Account) {
    const index = accounts.value.findIndex((a) => a.id === account.id)
    if (index === -1) accounts.value.push(account)
    else accounts.value[index] = account
    accounts.value.sort((a, b) => a.name.localeCompare(b.name))
  }

  async function fetchAll() {
    accounts.value = unwrap(await api.GET('/api/accounts'))
    loaded.value = true
  }

  async function fetchOne(id: string) {
    const account = unwrap(await api.GET('/api/accounts/{id}', { params: { path: { id } } }))
    upsert(account)
    return account
  }

  async function create(input: CreateAccountInput) {
    const account = unwrap(await api.POST('/api/accounts', { body: input }))
    upsert(account)
    return account
  }

  async function update(id: string, input: UpdateAccountInput) {
    const account = unwrap(await api.PUT('/api/accounts/{id}', { params: { path: { id } }, body: input }))
    upsert(account)
    return account
  }

  async function remove(id: string) {
    unwrap(await api.DELETE('/api/accounts/{id}', { params: { path: { id } } }))
    accounts.value = accounts.value.filter((a) => a.id !== id)
  }

  /** Checks settings that are not saved yet (add-account wizard). */
  async function testSettings(input: TestConnectionInput) {
    return unwrap(await api.POST('/api/accounts/test', { body: input }))
  }

  /** Checks a stored account; the server records the outcome as the account's status. */
  async function testStored(id: string) {
    const result = unwrap(await api.POST('/api/accounts/{id}/test', { params: { path: { id } } }))
    await fetchOne(id)
    return result
  }

  async function folders(id: string, counts = true) {
    return unwrap(await api.GET('/api/accounts/{id}/folders', { params: { path: { id }, query: { counts } } }))
  }

  /** Google / Microsoft sign-in that the server is set up for. */
  async function oauthProviders() {
    return unwrap(await api.GET('/api/oauth/providers'))
  }

  /**
   * Sends the browser to the provider's sign-in page. It comes back to /oauth/callback, which completes it.
   * With an account id, the existing account is reconnected instead of a new one added.
   */
  async function startOAuth(provider: OAuthProvider, options: { name?: string; accountId?: string } = {}) {
    const { authorizationUrl } = unwrap(
      await api.POST('/api/oauth/{provider}/start', {
        params: { path: { provider: provider.toLowerCase() } },
        body: { name: options.name || null, accountId: options.accountId ?? null },
      }),
    )
    window.location.assign(authorizationUrl)
  }

  async function completeOAuth(state: string, code: string) {
    const result = unwrap(await api.POST('/api/oauth/complete', { body: { state, code } }))
    upsert(result.account)
    return result
  }

  return {
    accounts,
    loaded,
    fetchAll,
    fetchOne,
    create,
    update,
    remove,
    testSettings,
    testStored,
    folders,
    oauthProviders,
    startOAuth,
    completeOAuth,
  }
})
