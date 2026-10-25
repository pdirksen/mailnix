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

  it('asks for the authenticator code when two-factor sign-in is on', async () => {
    apiMock.POST.mockImplementation((path: string) =>
      Promise.resolve(
        path === '/api/auth/login'
          ? apiResult(401, { title: 'Enter the code', errorCode: 'TwoFactorRequired' })
          : apiResult(200, testUser),
      ),
    )
    const { wrapper, router } = await mountLogin()
    const replace = vi.spyOn(router, 'replace').mockResolvedValue(undefined)

    await fillAndSubmit(wrapper, testUser.email, 'secret password')
    await vi.waitFor(() => expect(wrapper.find('#login-code').exists()).toBe(true))
    expect(useAuthStore().isAuthenticated).toBe(false)

    await wrapper.find('#login-code').setValue('123 456')
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(apiMock.POST).toHaveBeenLastCalledWith('/api/auth/login/two-factor', {
      body: { code: '123 456', isRecoveryCode: false, rememberMe: false },
    })
    expect(replace).toHaveBeenCalledWith('/')
    expect(useAuthStore().isAuthenticated).toBe(true)
  })

  it('accepts a recovery code and explains a wrong code', async () => {
    apiMock.POST.mockImplementation((path: string) =>
      Promise.resolve(
        path === '/api/auth/login'
          ? apiResult(401, { errorCode: 'TwoFactorRequired' })
          : apiResult(401, { title: 'The code is not valid.', errorCode: 'InvalidCode' }),
      ),
    )
    const { wrapper } = await mountLogin()
    await fillAndSubmit(wrapper, testUser.email, 'secret password')
    await vi.waitFor(() => expect(wrapper.find('#login-code').exists()).toBe(true))

    await wrapper.findAll('button').find((b) => b.text() === 'Use a recovery code instead')!.trigger('click')
    await wrapper.find('#login-code').setValue('abcde-12345')
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(apiMock.POST).toHaveBeenLastCalledWith('/api/auth/login/two-factor', {
      body: { code: 'abcde-12345', isRecoveryCode: true, rememberMe: false },
    })
    expect(wrapper.find('[data-testid="login-error"]').text()).toContain('This code is not valid.')
  })
})
