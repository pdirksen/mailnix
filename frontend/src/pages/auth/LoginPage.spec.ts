import { i18n } from '@/i18n'
import { routes } from '@/router'
import { useAuthStore } from '@/stores/auth'
import { apiMock, apiResult, testUser } from '@/test/apiMock'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createMemoryHistory, createRouter } from 'vue-router'
import LoginPage from './LoginPage.vue'

vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  api: (await import('@/test/apiMock')).apiMock,
}))

async function mountLogin(query = '') {
  const router = createRouter({ history: createMemoryHistory(), routes })
  await router.push(`/login${query}`)
  const wrapper = mount(LoginPage, { global: { plugins: [i18n, router] }, attachTo: document.body })
  return { wrapper, router }
}

async function fillAndSubmit(wrapper: Awaited<ReturnType<typeof mountLogin>>['wrapper'], email: string, password: string) {
  await wrapper.find('#email').setValue(email)
  await wrapper.find('#password').setValue(password)
  await wrapper.find('form').trigger('submit')
  await flushPromises()
}

describe('LoginPage', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.resetAllMocks()
    i18n.global.locale.value = 'en'
  })

  it('shows validation errors and does not call the API for invalid input', async () => {
    const { wrapper } = await mountLogin()

    await fillAndSubmit(wrapper, 'not-an-email', '')

    await vi.waitFor(() => expect(wrapper.text()).toContain('Enter a valid email address.'))
    expect(wrapper.text()).toContain('This field is required.')
    expect(apiMock.POST).not.toHaveBeenCalled()
  })

  it('signs in and follows a safe redirect', async () => {
    apiMock.POST.mockResolvedValue(apiResult(200, testUser))
    const { wrapper, router } = await mountLogin('?redirect=/not-there-yet')
    const replace = vi.spyOn(router, 'replace').mockResolvedValue(undefined)

    await fillAndSubmit(wrapper, testUser.email, 'secret password')

    await vi.waitFor(() => expect(replace).toHaveBeenCalledWith('/not-there-yet'))
    expect(apiMock.POST).toHaveBeenCalledWith('/api/auth/login', {
      body: { email: testUser.email, password: 'secret password', rememberMe: false },
    })
    expect(useAuthStore().isAuthenticated).toBe(true)
  })

  it('ignores an external redirect target', async () => {
    apiMock.POST.mockResolvedValue(apiResult(200, testUser))
    const { wrapper, router } = await mountLogin('?redirect=//evil.example')
    const replace = vi.spyOn(router, 'replace').mockResolvedValue(undefined)

    await fillAndSubmit(wrapper, testUser.email, 'secret password')

    await vi.waitFor(() => expect(replace).toHaveBeenCalledWith('/'))
  })

  it('shows the server error when sign-in fails', async () => {
    apiMock.POST.mockResolvedValue(apiResult(401, { title: 'Invalid email or password.' }))
    const { wrapper } = await mountLogin()

    await fillAndSubmit(wrapper, testUser.email, 'wrong password')

    await vi.waitFor(() =>
      expect(wrapper.find('[data-testid="login-error"]').text()).toContain('Invalid email or password.'),
    )
  })

  it('links to registration only when it is open', async () => {
    const closed = await mountLogin()
    expect(closed.wrapper.text()).not.toContain('Create account')

    useAuthStore().registrationOpen = true
    await flushPromises()
    expect(closed.wrapper.text()).toContain('Create account')
  })
})
