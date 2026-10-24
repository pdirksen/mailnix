import { i18n } from '@/i18n'
import { routes } from '@/router'
import { apiMock, apiResult } from '@/test/apiMock'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createMemoryHistory, createRouter } from 'vue-router'
import OAuthCallbackPage from './OAuthCallbackPage.vue'

vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  api: (await import('@/test/apiMock')).apiMock,
}))

vi.mock('vue-sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }))

async function open(query: string) {
  const router = createRouter({ history: createMemoryHistory(), routes })
  // Skip the auth guard: the page itself is what is tested here.
  router.beforeEach(() => true)
  await router.push(`/oauth/callback${query}`)
  const replace = vi.spyOn(router, 'replace')
  const wrapper = mount(OAuthCallbackPage, { global: { plugins: [i18n, router] } })
  await flushPromises()
  return { wrapper, router, replace }
}

describe('OAuthCallbackPage', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.resetAllMocks()
    i18n.global.locale.value = 'en'
  })

  it('completes the sign-in, removes the code from the address and opens the new account', async () => {
    apiMock.POST.mockResolvedValue(apiResult(200, { account: { id: 'acc-1', name: 'Gmail' }, created: true }))
    const { replace } = await open('?state=the-state&code=the-code')

    expect(apiMock.POST).toHaveBeenCalledWith('/api/oauth/complete', { body: { state: 'the-state', code: 'the-code' } })
    expect(replace).toHaveBeenNthCalledWith(1, { name: 'oauth-callback' })
    expect(replace).toHaveBeenLastCalledWith({ name: 'account-detail', params: { id: 'acc-1' }, query: { tab: 'settings' } })
  })

  it('explains a cancelled sign-in', async () => {
    const { wrapper } = await open('?error=access_denied&state=s')

    expect(wrapper.text()).toContain('The sign-in was cancelled.')
    expect(apiMock.POST).not.toHaveBeenCalled()
  })

  it('translates errors from the server', async () => {
    apiMock.POST.mockResolvedValue(apiResult(400, { title: 'The sign-in took too long', errorCode: 'OAuthStateInvalid' }))
    const { wrapper } = await open('?state=old&code=c')

    expect(wrapper.text()).toContain('The sign-in took too long or was started elsewhere.')
  })

  it('needs code and state', async () => {
    const { wrapper } = await open('')

    expect(wrapper.text()).toContain('This page was opened without sign-in data.')
  })
})
