import { i18n } from '@/i18n'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import { apiMock, apiResult, testUser } from '@/test/apiMock'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createMemoryHistory, createRouter } from 'vue-router'
import ConnectPage from './ConnectPage.vue'

vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  api: (await import('@/test/apiMock')).apiMock,
}))

const REDIRECT = 'https://claude.ai/api/mcp/auth_callback'
const QUERY = {
  response_type: 'code',
  client_id: 'abc123',
  redirect_uri: REDIRECT,
  code_challenge: 'c'.repeat(43),
  code_challenge_method: 'S256',
  state: 'xyz',
  scope: 'mailnix.read mailnix.write',
  resource: 'https://mail.example.com/mcp',
}

async function mountPage(query: Record<string, string> = QUERY) {
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/connect', name: 'connect', component: ConnectPage }] })
  await router.push({ name: 'connect', query })
  const wrapper = mount(ConnectPage, { global: { plugins: [i18n, router] } })
  await flushPromises()
  return wrapper
}

const assign = vi.fn()

beforeEach(() => {
  setActivePinia(createPinia())
  vi.resetAllMocks()
  useUiStore().setLocale('en')
  useAuthStore().user = { ...testUser }
  vi.stubGlobal('location', { ...window.location, assign })
})

afterEach(() => vi.unstubAllGlobals())

describe('ConnectPage', () => {
  it('shows the app and sends the browser back with the decision', async () => {
    apiMock.GET.mockResolvedValue(apiResult(200, { name: 'Claude', redirectHost: 'claude.ai', requestedScope: 'ReadWrite' }))
    const wrapper = await mountPage()

    expect(apiMock.GET).toHaveBeenCalledWith('/api/connect/app', {
      params: { query: { clientId: 'abc123', redirectUri: REDIRECT, scope: 'mailnix.read mailnix.write' } },
    })
    expect(wrapper.find('[data-testid=connect-app]').text()).toBe('Claude')
    expect(wrapper.text()).toContain('Signed in as user@example.com.')
    expect(wrapper.text()).toContain('you return to claude.ai')

    apiMock.POST.mockResolvedValue(apiResult(200, { redirectUrl: `${REDIRECT}?code=the-code&state=xyz` }))
    await wrapper.find('[data-testid=connect-allow]').trigger('click')
    await flushPromises()

    expect(apiMock.POST).toHaveBeenCalledWith('/api/connect', {
      body: {
        clientId: 'abc123',
        redirectUri: REDIRECT,
        responseType: 'code',
        codeChallenge: 'c'.repeat(43),
        codeChallengeMethod: 'S256',
        state: 'xyz',
        resource: 'https://mail.example.com/mcp',
        approve: true,
        allowChanges: true,
      },
    })
    expect(assign).toHaveBeenCalledWith(`${REDIRECT}?code=the-code&state=xyz`)
  })

  it('grants read-only access unless changes are allowed, and can decline', async () => {
    apiMock.GET.mockResolvedValue(apiResult(200, { name: 'Claude', redirectHost: 'claude.ai', requestedScope: 'Read' }))
    const wrapper = await mountPage()
    apiMock.POST.mockResolvedValue(apiResult(200, { redirectUrl: `${REDIRECT}?error=access_denied` }))

    await wrapper.find('[data-testid=connect-deny]').trigger('click')
    await flushPromises()

    expect(apiMock.POST.mock.calls[0]![1].body).toMatchObject({ approve: false, allowChanges: false })
    expect(assign).toHaveBeenCalledWith(`${REDIRECT}?error=access_denied`)
  })

  it('explains an unknown app or an incomplete link', async () => {
    apiMock.GET.mockResolvedValue(apiResult(400, { title: 'Unknown app', errorCode: 'ConnectInvalidApp' }))
    let wrapper = await mountPage()
    expect(wrapper.find('[data-testid=connect-error]').text()).toContain('the app is unknown')
    expect(wrapper.find('[data-testid=connect-allow]').exists()).toBe(false)

    wrapper = await mountPage({ client_id: 'abc123' })
    expect(wrapper.find('[data-testid=connect-error]').text()).toContain('incomplete')
  })
})
