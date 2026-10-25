import type { Schemas } from '@/api/client'
import { i18n } from '@/i18n'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import { apiMock, apiResult, testUser } from '@/test/apiMock'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { type Component, nextTick } from 'vue'
import { createMemoryHistory, createRouter } from 'vue-router'
import { toast } from 'vue-sonner'
import AddressesSettings from './AddressesSettings.vue'
import PreferencesSettings from './PreferencesSettings.vue'
import SecuritySettings from './SecuritySettings.vue'

vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  api: (await import('@/test/apiMock')).apiMock,
}))
vi.mock('vue-sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }))

type Profile = Schemas['ProfileResponse']

const baseProfile = (): Profile => ({
  email: testUser.email,
  displayName: 'Test User',
  timeZone: 'UTC',
  language: 'en',
  addresses: ['alias@example.com'],
  automaticAddresses: [testUser.email],
  organizationName: 'Default',
  multiTenant: false,
  twoFactor: { enabled: false, recoveryCodesLeft: 0 },
  createdAt: '2026-10-01T10:00:00Z',
})

async function mountPage(page: Component, profile: Profile = baseProfile()) {
  // Only what the pages navigate to; the real routes would start loading the whole app.
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', component: { template: '<div />' } },
      { path: '/login', name: 'login', component: { template: '<div />' } },
    ],
  })
  const wrapper = mount(page, { props: { profile }, global: { plugins: [i18n, router] }, attachTo: document.body })
  await flushPromises()
  return wrapper
}

/** Dialogs are teleported to the body. */
function inBody<T extends Element = HTMLElement>(selector: string) {
  return document.body.querySelector<T>(selector)
}

async function typeInto(selector: string, value: string) {
  const input = inBody<HTMLInputElement>(selector)!
  input.value = value
  input.dispatchEvent(new Event('input'))
  await nextTick()
}

beforeEach(() => {
  document.body.innerHTML = ''
  setActivePinia(createPinia())
  vi.resetAllMocks()
  useUiStore().setLocale('en')
  useAuthStore().user = { ...testUser }
})

describe('AddressesSettings', () => {
  it('adds and removes own addresses, refusing duplicates', async () => {
    const wrapper = await mountPage(AddressesSettings)
    apiMock.PUT.mockResolvedValue(apiResult(200, { ...baseProfile(), addresses: ['alias@example.com', 'news@example.com'] }))

    await wrapper.find('#new-address').setValue('ALIAS@example.com')
    await wrapper.find('form').trigger('submit')
    expect(wrapper.text()).toContain('This address is already in the list.')
    expect(apiMock.PUT).not.toHaveBeenCalled()

    await wrapper.find('#new-address').setValue('News@Example.com ')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(apiMock.PUT).toHaveBeenCalledWith('/api/profile/addresses', { body: { addresses: ['alias@example.com', 'news@example.com'] } })

    await wrapper.find('[aria-label="Remove alias@example.com"]').trigger('click')
    await flushPromises()
    expect(apiMock.PUT).toHaveBeenLastCalledWith('/api/profile/addresses', { body: { addresses: [] } })
  })
})

describe('PreferencesSettings', () => {
  it('switches and saves the language, and offers the browser time zone', async () => {
    apiMock.PUT.mockResolvedValue(apiResult(200, { ...baseProfile(), language: 'de' }))
    const wrapper = await mountPage(PreferencesSettings, { ...baseProfile(), timeZone: 'Pacific/Chatham' })

    expect(wrapper.find('[data-testid="browser-zone-hint"]').exists()).toBe(true)

    await wrapper.find('[data-testid="language-de"]').trigger('click')
    await flushPromises()

    expect(useUiStore().locale).toBe('de')
    expect(apiMock.PUT).toHaveBeenCalledWith('/api/profile', { body: { language: 'de' } })
  })

  it('keeps the theme in this browser only', async () => {
    const wrapper = await mountPage(PreferencesSettings)

    await wrapper.find('[data-testid="theme-dark"]').trigger('click')

    expect(useUiStore().theme).toBe('dark')
    expect(apiMock.PUT).not.toHaveBeenCalled()
  })
})

describe('SecuritySettings', () => {
  it('checks the new password and shows a wrong current password at the field', async () => {
    const wrapper = await mountPage(SecuritySettings)
    apiMock.POST.mockResolvedValue(apiResult(400, { title: 'wrong', errorCode: 'WrongPassword' }))

    await wrapper.find('#current-password').setValue('old password 1')
    await wrapper.find('#new-password').setValue('short')
    await wrapper.find('[data-testid="change-password"]').trigger('submit')
    expect(wrapper.text()).toContain('Use at least 10 characters.')
    expect(apiMock.POST).not.toHaveBeenCalled()

    await wrapper.find('#new-password').setValue('a much longer passphrase')
    await wrapper.find('#repeat-password').setValue('a much longer passphrase')
    await wrapper.find('[data-testid="change-password"]').trigger('submit')
    await flushPromises()

    expect(apiMock.POST).toHaveBeenCalledWith('/api/profile/password', {
      body: { currentPassword: 'old password 1', newPassword: 'a much longer passphrase' },
    })
    expect(wrapper.find('#current-password-error').text()).toBe('The password is not correct.')
  })

  it('sets up two-factor sign-in: QR code, confirmation code, recovery codes', async () => {
    const wrapper = await mountPage(SecuritySettings)
    apiMock.POST.mockImplementation((path: string) =>
      Promise.resolve(
        path === '/api/profile/two-factor/setup'
          ? apiResult(200, { sharedKey: 'JBSW Y3DP EHPK 3PXP', authenticatorUri: 'otpauth://totp/mailnix:user?secret=JBSWY3DPEHPK3PXP' })
          : apiResult(200, { codes: ['AAAAA-11111', 'BBBBB-22222'] }),
      ),
    )
    apiMock.GET.mockResolvedValue(apiResult(200, { ...baseProfile(), twoFactor: { enabled: true, recoveryCodesLeft: 2 } }))

    await wrapper.find('[data-testid="two-factor-setup"]').trigger('click')
    await flushPromises()
    expect(inBody('svg[role="img"]')?.getAttribute('aria-label')).toBe('QR code for the authenticator app')

    ;[...document.body.querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Next')!.click()
    await flushPromises()
    await typeInto('#two-factor-code', '12345')
    inBody('[data-testid="two-factor-verify"]')!.click()
    await flushPromises()
    expect(document.body.textContent).toContain('Enter the 6 digits the app shows.')

    await typeInto('#two-factor-code', '123 456')
    inBody('[data-testid="two-factor-verify"]')!.click()
    await flushPromises()

    expect(apiMock.POST).toHaveBeenCalledWith('/api/profile/two-factor/enable', { body: { code: '123456' } })
    expect(inBody('[data-testid="recovery-codes"]')!.textContent).toContain('AAAAA-11111')
  })

  it('deletes the account only with the confirmation word and the password', async () => {
    const wrapper = await mountPage(SecuritySettings)
    apiMock.POST.mockResolvedValue(apiResult(204))

    await wrapper.find('[data-testid="delete-account"]').trigger('click')
    await flushPromises()
    await typeInto('#confirm-password', 'my password 123')
    inBody('[data-testid="confirm-password-submit"]')!.click()
    await flushPromises()
    expect(document.body.textContent).toContain('Type DELETE to confirm')
    expect(apiMock.POST).not.toHaveBeenCalled()

    await typeInto('#delete-confirm', 'delete')
    inBody('[data-testid="confirm-password-submit"]')!.click()
    await flushPromises()

    expect(apiMock.POST).toHaveBeenCalledWith('/api/profile/delete', { body: { password: 'my password 123' } })
    expect(useAuthStore().isAuthenticated).toBe(false)
    expect(toast.success).toHaveBeenCalledWith('Your account was deleted.')
  })

  it('does not offer deleting the installation admin', async () => {
    useAuthStore().user = { ...testUser, roles: ['Admin', 'TenantAdmin', 'User'] }
    const wrapper = await mountPage(SecuritySettings)

    expect(wrapper.find('[data-testid="delete-account"]').exists()).toBe(false)
    expect(wrapper.text()).toContain('You administer this installation')
  })
})
