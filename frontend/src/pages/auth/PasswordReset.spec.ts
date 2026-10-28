import { i18n } from '@/i18n'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import { apiMock, apiResult } from '@/test/apiMock'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { type Component } from 'vue'
import { createMemoryHistory, createRouter } from 'vue-router'
import ForgotPasswordPage from './ForgotPasswordPage.vue'
import LoginPage from './LoginPage.vue'
import ResetPasswordPage from './ResetPasswordPage.vue'

vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  api: (await import('@/test/apiMock')).apiMock,
}))

const blank = { template: '<div />' }

/** Only the auth routes; the real route table would start loading the whole app. */
async function mountAt(page: Component, path: string) {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', component: blank },
      { path: '/login', name: 'login', component: blank },
      { path: '/register', name: 'register', component: blank },
      { path: '/forgot-password', name: 'forgot-password', component: blank },
      { path: '/reset-password', name: 'reset-password', component: blank },
    ],
  })
  await router.push(path)
  const replace = vi.spyOn(router, 'replace').mockResolvedValue(undefined)
  const wrapper = mount(page, { global: { plugins: [i18n, router] }, attachTo: document.body })
  await flushPromises()
  return { wrapper, replace }
}

beforeEach(() => {
  setActivePinia(createPinia())
  vi.resetAllMocks()
  useUiStore().setLocale('en')
  useAuthStore().passwordResetAvailable = true
})

describe('LoginPage', () => {
  it('offers "Forgot password?" only when the server can send email', async () => {
    const { wrapper } = await mountAt(LoginPage, '/login')
    expect(wrapper.text()).toContain('Forgot password?')

    useAuthStore().passwordResetAvailable = false
    await flushPromises()
    expect(wrapper.text()).not.toContain('Forgot password?')
  })

  it('confirms a reset password', async () => {
    const { wrapper } = await mountAt(LoginPage, '/login?reset=1')
    expect(wrapper.find('[data-testid="password-reset-done"]').text()).toContain('Your password was changed.')
  })
})

describe('ForgotPasswordPage', () => {
  it('asks for the link in the page language and shows the same answer for any address', async () => {
    apiMock.POST.mockResolvedValue(apiResult(202))
    useUiStore().setLocale('de')
    const { wrapper } = await mountAt(ForgotPasswordPage, '/forgot-password?email=pat%40example.com')
    expect(wrapper.find<HTMLInputElement>('#email').element.value).toBe('pat@example.com')

    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(apiMock.POST).toHaveBeenCalledWith('/api/auth/forgot-password', { body: { email: 'pat@example.com', language: 'de' } })
    expect(wrapper.find('[data-testid="reset-link-sent"]').text()).toContain('Falls es ein Konto für pat@example.com gibt')
  })

  it('checks the address and explains when reset is not available', async () => {
    const { wrapper } = await mountAt(ForgotPasswordPage, '/forgot-password')
    await wrapper.find('#email').setValue('not an address')
    await wrapper.find('form').trigger('submit')
    expect(wrapper.text()).toContain('Enter a valid email address.')
    expect(apiMock.POST).not.toHaveBeenCalled()

    useAuthStore().passwordResetAvailable = false
    await flushPromises()
    expect(wrapper.find('[data-testid="reset-unavailable"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="send-reset-link"]').attributes('disabled')).toBeDefined()
  })
})

describe('ResetPasswordPage', () => {
  it('sets the new password from the link and goes to sign-in', async () => {
    apiMock.POST.mockResolvedValue(apiResult(204))
    const { wrapper, replace } = await mountAt(ResetPasswordPage, '/reset-password?email=pat%40example.com&token=abc123')
    expect(wrapper.text()).toContain('For your account pat@example.com.')

    await wrapper.find('#new-password').setValue('short')
    await wrapper.find('form').trigger('submit')
    expect(wrapper.text()).toContain('Use at least 10 characters.')

    await wrapper.find('#new-password').setValue('a brand new password')
    await wrapper.find('#repeat-password').setValue('a brand new password')
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(apiMock.POST).toHaveBeenCalledWith('/api/auth/reset-password', {
      body: { email: 'pat@example.com', token: 'abc123', newPassword: 'a brand new password' },
    })
    expect(replace).toHaveBeenCalledWith({ name: 'login', query: { reset: '1' } })
  })

  it('offers a new link when this one is used up or broken', async () => {
    apiMock.POST.mockResolvedValue(apiResult(400, { title: 'invalid', errorCode: 'ResetLinkInvalid' }))
    const { wrapper } = await mountAt(ResetPasswordPage, '/reset-password?email=pat%40example.com&token=old')
    await wrapper.find('#new-password').setValue('a brand new password')
    await wrapper.find('#repeat-password').setValue('a brand new password')
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(wrapper.find('[data-testid="reset-link-invalid"]').text()).toContain('This link is no longer valid.')
    expect(wrapper.find('a').attributes('href')).toBe('/forgot-password?email=pat@example.com')

    const incomplete = await mountAt(ResetPasswordPage, '/reset-password')
    expect(incomplete.wrapper.find('[data-testid="reset-link-invalid"]').exists()).toBe(true)
  })
})
