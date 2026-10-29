import { i18n } from '@/i18n'
import { routes } from '@/router'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import { apiMock, apiResult, testUser } from '@/test/apiMock'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createMemoryHistory, createRouter } from 'vue-router'
import RegisterPage from './RegisterPage.vue'

vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  api: (await import('@/test/apiMock')).apiMock,
}))

interface Setup {
  registrationOpen?: boolean
  multiTenant?: boolean
  setupCodeRequired?: boolean
  invitation?: { tenantName: string; email: string | null } | null
}

/** The server settings (as loaded by the router guard); the invitation lookup answers 404 for `invitation: null`. */
function serve({ registrationOpen = true, multiTenant = false, setupCodeRequired = false, invitation }: Setup) {
  Object.assign(useAuthStore(), { registrationOpen, multiTenant, setupCodeRequired, initialized: true })
  apiMock.GET.mockImplementation((path: string) => {
    if (path === '/api/auth/invitations/{token}') {
      return Promise.resolve(invitation ? apiResult(200, invitation) : apiResult(404))
    }
    return Promise.resolve(apiResult(404))
  })
}

async function mountRegister(query = '') {
  const router = createRouter({ history: createMemoryHistory(), routes })
  await router.push(`/register${query}`)
  // Signed up: the dashboard is not part of these tests (and would still be loading when they end).
  vi.spyOn(router, 'replace').mockResolvedValue(undefined)
  const wrapper = mount(RegisterPage, { global: { plugins: [i18n, router] }, attachTo: document.body })
  await flushPromises()
  return { wrapper, router }
}

async function submit(wrapper: Awaited<ReturnType<typeof mountRegister>>['wrapper'], email?: string) {
  if (email !== undefined) await wrapper.find('#email').setValue(email)
  await wrapper.find('#password').setValue('correct horse battery')
  await wrapper.find('#confirm-password').setValue('correct horse battery')
  await wrapper.find('form').trigger('submit')
  await flushPromises()
}

describe('RegisterPage', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.resetAllMocks()
    useUiStore().setLocale('en')
  })

  it('asks for the organization name when every signup founds an organization', async () => {
    serve({ multiTenant: true })
    apiMock.POST.mockResolvedValue(apiResult(200, testUser))
    const { wrapper } = await mountRegister()

    await wrapper.find('#organization').setValue('  Acme ')
    await submit(wrapper, 'founder@acme.test')

    await vi.waitFor(() =>
      expect(apiMock.POST).toHaveBeenCalledWith('/api/auth/register', {
        body: { email: 'founder@acme.test', password: 'correct horse battery', displayName: null, organizationName: 'Acme', inviteToken: null, setupCode: null, timeZone: expect.any(String), language: 'en' },
      }),
    )
  })

  it('asks for the setup code for the first account, prefilled from the link in the server log', async () => {
    serve({ setupCodeRequired: true })
    apiMock.POST.mockResolvedValue(apiResult(200, testUser))
    const { wrapper } = await mountRegister('?setup=ABCD-EFGH-JKMN')

    expect((wrapper.find('#setup-code').element as HTMLInputElement).value).toBe('ABCD-EFGH-JKMN')
    await submit(wrapper, 'owner@example.com')

    await vi.waitFor(() =>
      expect(apiMock.POST).toHaveBeenCalledWith('/api/auth/register', {
        body: expect.objectContaining({ email: 'owner@example.com', setupCode: 'ABCD-EFGH-JKMN' }),
      }),
    )
  })

  it('translates a wrong setup code and needs none once accounts exist', async () => {
    serve({ setupCodeRequired: true })
    apiMock.POST.mockResolvedValue(apiResult(403, { title: 'Forbidden', errorCode: 'SetupCodeInvalid' }))
    const { wrapper } = await mountRegister()
    await wrapper.find('#setup-code').setValue('WRONG')
    await submit(wrapper, 'owner@example.com')

    await vi.waitFor(() => expect(wrapper.find('[data-testid=register-error]').text()).toContain('The setup code is not correct'))

    setActivePinia(createPinia())
    serve({ setupCodeRequired: false })
    expect((await mountRegister()).wrapper.find('#setup-code').exists()).toBe(false)
  })

  it('does not ask for an organization on a self-hosted installation', async () => {
    serve({ multiTenant: false })
    const { wrapper } = await mountRegister()

    expect(wrapper.find('#organization').exists()).toBe(false)
  })

  it('joins the inviting organization with the invited address', async () => {
    serve({ registrationOpen: false, multiTenant: true, invitation: { tenantName: 'Acme', email: 'carol@acme.test' } })
    apiMock.POST.mockResolvedValue(apiResult(200, testUser))
    const { wrapper, router } = await mountRegister('?invite=tok3n')

    expect(router.currentRoute.value.name).toBe('register')
    expect(wrapper.text()).toContain('Join Acme')
    expect(wrapper.find('#organization').exists()).toBe(false)
    const email = wrapper.find<HTMLInputElement>('#email')
    expect(email.element.value).toBe('carol@acme.test')
    expect(email.attributes('readonly')).toBeDefined()

    await submit(wrapper)

    await vi.waitFor(() =>
      expect(apiMock.POST).toHaveBeenCalledWith('/api/auth/register', {
        body: { email: 'carol@acme.test', password: 'correct horse battery', displayName: null, organizationName: null, inviteToken: 'tok3n', setupCode: null, timeZone: expect.any(String), language: 'en' },
      }),
    )
  })

  it('explains invalid invitations and translates the server error', async () => {
    serve({ invitation: null })
    apiMock.POST.mockResolvedValue(apiResult(400, { title: 'This invitation is no longer valid.', errorCode: 'InvitationInvalid' }))
    useUiStore().setLocale('de')
    const { wrapper } = await mountRegister('?invite=old')

    expect(wrapper.find('[data-testid="invitation-invalid"]').text()).toContain('Diese Einladung ist nicht mehr gültig.')

    await submit(wrapper, 'late@acme.test')

    await vi.waitFor(() =>
      expect(wrapper.find('[data-testid="register-error"]').text()).toContain('Diese Einladung ist nicht mehr gültig.'),
    )
  })
})
