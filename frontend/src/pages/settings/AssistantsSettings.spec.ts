import type { Schemas } from '@/api/client'
import { i18n } from '@/i18n'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import { apiMock, apiResult, testUser } from '@/test/apiMock'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { nextTick } from 'vue'
import { toast } from 'vue-sonner'
import AssistantsSettings from './AssistantsSettings.vue'

vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  api: (await import('@/test/apiMock')).apiMock,
}))
vi.mock('vue-sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }))

type ApiToken = Schemas['ApiTokenResponse']

const MCP_URL = 'https://mail.example.com/mcp'

const token = (overrides: Partial<ApiToken> = {}): ApiToken => ({
  id: 'token-1',
  kind: 'Personal',
  name: 'Laptop',
  scope: 'Read',
  tokenHint: 'mnx_pat_AbCd',
  createdAt: '2026-10-20T10:00:00Z',
  lastUsedAt: null,
  expiresAt: '2027-01-18T10:00:00Z',
  expired: false,
  ...overrides,
})

const profile = {} as Schemas['ProfileResponse']

async function mountPage(tokens: ApiToken[]) {
  apiMock.GET.mockResolvedValue(apiResult(200, { mcpUrl: MCP_URL, tokens }))
  const wrapper = mount(AssistantsSettings, { props: { profile }, global: { plugins: [i18n] }, attachTo: document.body })
  await flushPromises()
  return wrapper
}

function inBody<T extends Element = HTMLElement>(selector: string) {
  return document.body.querySelector<T>(selector)
}

beforeEach(() => {
  document.body.innerHTML = ''
  setActivePinia(createPinia())
  vi.resetAllMocks()
  useUiStore().setLocale('en')
  useAuthStore().user = { ...testUser }
})

describe('AssistantsSettings', () => {
  it('shows the server address and lists tokens and connected apps', async () => {
    const wrapper = await mountPage([
      token(),
      token({ id: 'conn-1', kind: 'Connection', name: 'Claude', scope: 'ReadWrite', tokenHint: null, lastUsedAt: '2026-10-28T09:00:00Z' }),
    ])

    expect(wrapper.find('[data-testid=mcp-url]').text()).toContain(MCP_URL)
    const list = wrapper.find('[data-testid=token-list]').text()
    expect(list).toContain('Laptop')
    expect(list).toContain('mnx_pat_AbCd…')
    expect(list).toContain('Read only')
    expect(list).toContain('Never used')
    expect(list).toContain('Claude')
    expect(list).toContain('Connected app')
    expect(list).toContain('Read and draft')
    expect(wrapper.find('[data-testid=revoke-conn-1]').text()).toContain('Disconnect')
  })

  it('creates a token and shows it once with ready-made configurations', async () => {
    const wrapper = await mountPage([])
    expect(wrapper.find('[data-testid=tokens-empty]').exists()).toBe(true)

    await wrapper.find('[data-testid=create-token]').trigger('click')
    await flushPromises()
    inBody<HTMLButtonElement>('[data-testid=create-token-submit]')!.click()
    await flushPromises()
    expect(inBody('[role=dialog]')!.textContent).toContain('This field is required.')
    expect(apiMock.POST).not.toHaveBeenCalled()

    const name = inBody<HTMLInputElement>('#token-name')!
    name.value = 'Claude Code'
    name.dispatchEvent(new Event('input'))
    inBody<HTMLButtonElement>('#scope-ReadWrite')!.click()
    inBody<HTMLButtonElement>('#expiry-never')!.click()
    await nextTick()

    apiMock.POST.mockResolvedValue(
      apiResult(200, { token: token({ name: 'Claude Code', scope: 'ReadWrite', expiresAt: null }), secret: 'mnx_pat_secret', mcpUrl: MCP_URL }),
    )
    inBody<HTMLButtonElement>('[data-testid=create-token-submit]')!.click()
    await flushPromises()

    expect(apiMock.POST).toHaveBeenCalledWith('/api/tokens', { body: { name: 'Claude Code', scope: 'ReadWrite', expiresInDays: null } })
    expect(inBody('[data-testid=created-token]')!.textContent).toContain('mnx_pat_secret')
    expect(inBody('[data-testid=snippet-claudeCode]')!.textContent).toContain(
      `claude mcp add --transport http mailnix ${MCP_URL} --header "Authorization: Bearer mnx_pat_secret"`,
    )
    expect(wrapper.find('[data-testid=token-list]').text()).toContain('Claude Code')
  })

  it('revokes a token after confirmation', async () => {
    const wrapper = await mountPage([token()])
    apiMock.DELETE.mockResolvedValue(apiResult(204))

    await wrapper.find('[data-testid=revoke-token-1]').trigger('click')
    await flushPromises()
    expect(inBody('[role=alertdialog]')!.textContent).toContain('Revoke “Laptop”?')
    inBody<HTMLButtonElement>('[data-testid=confirm-revoke]')!.click()
    await flushPromises()

    expect(apiMock.DELETE).toHaveBeenCalledWith('/api/tokens/{id}', { params: { path: { id: 'token-1' } } })
    expect(toast.success).toHaveBeenCalledWith('Token “Laptop” revoked.')
    expect(wrapper.find('[data-testid=tokens-empty]').exists()).toBe(true)
  })
})
