import { i18n } from '@/i18n'
import { routes } from '@/router'
import { apiMock, apiResult } from '@/test/apiMock'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createMemoryHistory, createRouter } from 'vue-router'
import AddAccountPage from './AddAccountPage.vue'

vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  api: (await import('@/test/apiMock')).apiMock,
}))

vi.mock('vue-sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }))

const folders = [
  { path: 'INBOX', name: 'INBOX', parentPath: null, canSelect: true, specialUse: 'Inbox', messages: null, unread: null },
  { path: 'INBOX.Newsletters', name: 'Newsletters', parentPath: 'INBOX', canSelect: true, specialUse: null, messages: null, unread: null },
  { path: 'INBOX.Trash', name: 'Trash', parentPath: 'INBOX', canSelect: true, specialUse: 'Trash', messages: null, unread: null },
]

const successfulTest = {
  success: true,
  errorCode: null,
  errorMessage: null,
  mailbox: {
    capabilities: { all: ['Idle', 'Move'] },
    delimiter: '.',
    folders,
    specialFolders: { sent: null, drafts: null, trash: 'INBOX.Trash', junk: null, archive: null },
  },
}

async function mountWizard() {
  const router = createRouter({ history: createMemoryHistory(), routes })
  await router.push('/accounts/new')
  const wrapper = mount(AddAccountPage, { global: { plugins: [i18n, router] }, attachTo: document.body })
  return { wrapper, router }
}

async function fillServerStep(wrapper: Awaited<ReturnType<typeof mountWizard>>['wrapper']) {
  await wrapper.find('[data-testid="provider-Imap"]').trigger('click')
  await wrapper.find('#account-host').setValue('imap.example.com')
  await wrapper.find('#account-username').setValue('me@example.com')
  await wrapper.find('#account-password').setValue('secret')
  await wrapper.find('form').trigger('submit')
  await flushPromises()
}

describe('AddAccountPage', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.resetAllMocks()
    i18n.global.locale.value = 'en'
  })

  it('offers Google and Microsoft only when the server is set up for them', async () => {
    apiMock.GET.mockResolvedValue(apiResult(200, [{ provider: 'Google', configured: true }, { provider: 'Microsoft', configured: false }]))
    const { wrapper } = await mountWizard()
    await flushPromises()

    expect(wrapper.find('[data-testid="provider-Google"]').attributes('disabled')).toBeUndefined()
    expect(wrapper.find('[data-testid="provider-Microsoft"]').attributes('disabled')).toBeDefined()
    expect(wrapper.find('[data-testid="provider-Microsoft"]').text()).toContain('Not set up')
  })

  it('sends the browser to the provider to sign in', async () => {
    apiMock.GET.mockResolvedValue(apiResult(200, [{ provider: 'Google', configured: true }]))
    apiMock.POST.mockResolvedValue(apiResult(200, { authorizationUrl: 'https://accounts.google.com/o/oauth2/v2/auth?state=s' }))
    const assign = vi.fn()
    vi.stubGlobal('location', { ...window.location, assign })
    const { wrapper } = await mountWizard()
    await flushPromises()

    await wrapper.find('[data-testid="provider-Google"]').trigger('click')
    expect(wrapper.text()).toContain('Sign in with Google')
    await wrapper.find('#oauth-name').setValue('Private Gmail')
    await wrapper.find('[data-testid="oauth-Google"]').trigger('click')
    await flushPromises()

    expect(apiMock.POST).toHaveBeenCalledWith('/api/oauth/{provider}/start', {
      params: { path: { provider: 'google' } },
      body: { name: 'Private Gmail', accountId: null },
    })
    expect(assign).toHaveBeenCalledWith('https://accounts.google.com/o/oauth2/v2/auth?state=s')
    vi.unstubAllGlobals()
  })

  it('validates the server step before connecting', async () => {
    const { wrapper } = await mountWizard()
    await wrapper.find('[data-testid="provider-Imap"]').trigger('click')

    await wrapper.find('form').trigger('submit')

    expect(wrapper.text()).toContain('This field is required.')
    expect(apiMock.POST).not.toHaveBeenCalled()
  })

  it('tests the connection and creates the account with the chosen folders', async () => {
    apiMock.POST.mockImplementation(async (path: string) =>
      path === '/api/accounts/test' ? apiResult(200, successfulTest) : apiResult(201, { id: 'new-id', name: 'me@example.com' }),
    )
    const { wrapper, router } = await mountWizard()
    const push = vi.spyOn(router, 'push')

    await fillServerStep(wrapper)

    expect(apiMock.POST).toHaveBeenCalledWith('/api/accounts/test', {
      body: { host: 'imap.example.com', port: 993, security: 'SslOnConnect', username: 'me@example.com', password: 'secret' },
    })
    expect(wrapper.find('[data-testid="connection-ok"]').text()).toContain('imap.example.com')

    await wrapper.findAll('button').find((b) => b.text() === 'Next')!.trigger('click')
    await wrapper.find('[aria-label="INBOX.Newsletters"]').trigger('click')
    await wrapper.find('[data-testid="create-account"]').trigger('click')
    await flushPromises()

    expect(apiMock.POST).toHaveBeenLastCalledWith('/api/accounts', {
      body: expect.objectContaining({
        name: 'me@example.com', // defaults to the username
        watchedFolders: ['INBOX', 'INBOX.Newsletters'],
        specialFolders: { sent: null, drafts: null, trash: 'INBOX.Trash', junk: null, archive: null },
        blastRadiusLimit: 200,
        purgeTrashAfterDays: null,
      }),
    })
    expect(push).toHaveBeenCalledWith({ name: 'account-detail', params: { id: 'new-id' } })
  })

  it('explains connection failures in the user language and allows a retry', async () => {
    apiMock.POST.mockResolvedValue(
      apiResult(200, { success: false, errorCode: 'AuthenticationFailed', errorMessage: 'server said no', mailbox: null }),
    )
    i18n.global.locale.value = 'de'
    const { wrapper } = await mountWizard()

    await fillServerStep(wrapper)

    const error = wrapper.find('[data-testid="connection-error"]')
    expect(error.text()).toContain('Der Server hat Benutzername oder Passwort abgelehnt.')
    expect(error.text()).toContain('server said no')
    expect(wrapper.findAll('button').find((b) => b.text() === 'Weiter')!.attributes('disabled')).toBeDefined()

    apiMock.POST.mockResolvedValue(apiResult(200, successfulTest))
    await wrapper.findAll('button').find((b) => b.text() === 'Erneut versuchen')!.trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-testid="connection-ok"]').exists()).toBe(true)
  })
})
