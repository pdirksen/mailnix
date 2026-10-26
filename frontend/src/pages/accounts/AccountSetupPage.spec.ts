import { i18n } from '@/i18n'
import { routes } from '@/router'
import { apiMock, apiResult } from '@/test/apiMock'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createMemoryHistory, createRouter } from 'vue-router'
import AccountSetupPage from './AccountSetupPage.vue'

vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  api: (await import('@/test/apiMock')).apiMock,
}))

vi.mock('vue-sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }))

const ACCOUNT_ID = '0192a000-0000-7000-8000-000000000001'

const account = { id: ACCOUNT_ID, name: 'Work', provider: 'Imap', enabled: true, watchedFolders: ['INBOX'] }

const blueprints = [
  {
    id: 'minimal',
    folders: [
      { key: 'newsletters', suffix: null, defaultOn: true },
      { key: 'notifications', suffix: null, defaultOn: true },
      { key: 'archive', suffix: '{year}', defaultOn: true },
    ],
    rules: [
      { key: 'newsletters', templateId: 'newsletters-to-folder', folder: 'newsletters', list: null, defaultOn: true, parameters: [] },
      { key: 'notifications', templateId: 'notifications', folder: 'notifications', list: null, defaultOn: true, parameters: [] },
      {
        key: 'archive',
        templateId: 'archive-by-year',
        folder: 'archive',
        list: null,
        defaultOn: true,
        parameters: [{ name: 'days', type: 'Days', default: '90' }],
      },
    ],
    lists: [],
  },
]

const analysis = {
  folder: 'INBOX',
  totalInFolder: 40,
  examined: 40,
  sortedByExistingRules: 0,
  unsorted: 30,
  fetchedAt: '2026-10-26T10:00:00Z',
  fromCache: false,
  rules: [
    { key: 'newsletters', matched: 8, sorted: 8, blocked: 0, examples: [{ from: 'news@shop.example', subject: 'Autumn sale', date: null, targetFolder: 'INBOX.Newsletters' }] },
    { key: 'archive', matched: 2, sorted: 2, blocked: 0, examples: [] },
  ],
  unsortedDomains: [{ domain: 'example.com', count: 12 }],
}

const applyResult = {
  rules: [
    { key: 'newsletters', id: 'r1', name: 'Newsletters into a folder' },
    { key: 'archive', id: 'r2', name: 'Archive by year' },
  ],
  createdListIds: [],
  run: null,
  folders: [{ path: 'INBOX.Newsletters', created: true, error: null }],
}

function mockApi() {
  apiMock.GET.mockImplementation(async (path: string) => {
    switch (path) {
      case '/api/accounts/{id}':
        return apiResult(200, account)
      case '/api/assistant/blueprints':
        return apiResult(200, blueprints)
      case '/api/accounts/{id}/folders':
        return apiResult(200, {
          delimiter: '.',
          folders: [{ path: 'INBOX.Newsletters', name: 'Newsletters', parentPath: 'INBOX', canSelect: true, specialUse: null, messages: null, unread: null }],
          specialFolders: {},
        })
      default:
        return apiResult(200, [])
    }
  })
  apiMock.POST.mockImplementation(async (path: string) => {
    switch (path) {
      case '/api/accounts/{id}/assistant/analyze':
        return apiResult(200, analysis)
      case '/api/accounts/{id}/assistant/apply':
        return apiResult(200, applyResult)
      default:
        return apiResult(204)
    }
  })
}

async function mountAssistant() {
  const router = createRouter({ history: createMemoryHistory(), routes })
  await router.push(`/accounts/${ACCOUNT_ID}/setup`)
  const wrapper = mount(AccountSetupPage, { props: { id: ACCOUNT_ID }, global: { plugins: [i18n, router] }, attachTo: document.body })
  await flushPromises()
  return wrapper
}

const expectedSelection = {
  blueprintId: 'minimal',
  folders: { newsletters: 'INBOX/Newsletters', archive: 'INBOX/Archive/{year}' },
  rules: [
    { key: 'newsletters', name: 'Newsletters into a folder', parameters: {} },
    { key: 'archive', name: 'Archive by year', parameters: { days: '90' } },
  ],
  lists: {},
}

describe('AccountSetupPage', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.resetAllMocks()
    i18n.global.locale.value = 'en'
    mockApi()
  })

  it('suggests the rules of the chosen structure and tries them on the inbox', async () => {
    const wrapper = await mountAssistant()

    await wrapper.find('[data-testid="blueprint-minimal"]').trigger('click')
    expect(wrapper.find('[data-testid="assistant-folders"]').text()).toContain('exists')
    await wrapper.find('[data-testid="folder-toggle-notifications"]').trigger('click')
    await wrapper.find('[data-testid="assistant-next"]').trigger('click')

    // The notifications rule needs its folder, which was switched off.
    expect(wrapper.find('[data-testid="rule-toggle-notifications"]').attributes('disabled')).toBeDefined()
    await wrapper.find('[data-testid="assistant-analyze"]').trigger('click')
    await flushPromises()

    expect(apiMock.POST).toHaveBeenCalledWith('/api/accounts/{id}/assistant/analyze', {
      params: { path: { id: ACCOUNT_ID } },
      body: { selection: expectedSelection, limit: 300, refresh: false },
    })
    expect(wrapper.find('[data-testid="rule-result-newsletters"]').text()).toContain('8 matches')
    expect(wrapper.text()).toContain('Autumn sale')
    expect(wrapper.find('[data-testid="assistant-domains"]').text()).toContain('example.com')
  })

  it('creates the rules in dry-run mode and switches them live afterwards', async () => {
    const wrapper = await mountAssistant()
    await wrapper.find('[data-testid="blueprint-minimal"]').trigger('click')
    await wrapper.find('[data-testid="folder-toggle-notifications"]').trigger('click')
    await wrapper.find('[data-testid="assistant-next"]').trigger('click')
    await wrapper.find('[data-testid="assistant-next"]').trigger('click')

    expect(wrapper.text()).toContain('2 folders, 2 rules and 0 new lists.')
    await wrapper.find('[data-testid="assistant-apply"]').trigger('click')
    await flushPromises()

    expect(apiMock.POST).toHaveBeenCalledWith('/api/accounts/{id}/assistant/apply', {
      params: { path: { id: ACCOUNT_ID } },
      body: { selection: expectedSelection, dryRun: true, createFolders: true, runOnInboxLimit: null },
    })
    expect(wrapper.find('[data-testid="assistant-done"]').text()).toContain('2 rules created')

    await wrapper.find('[data-testid="assistant-go-live"]').trigger('click')
    await flushPromises()

    expect(apiMock.POST).toHaveBeenCalledWith('/api/rules/dry-run', { body: { ruleIds: ['r1', 'r2'], dryRun: false } })
    expect(wrapper.find('[data-testid="assistant-go-live"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="assistant-sort-now"]').exists()).toBe(true)
  })
})
