import { i18n } from '@/i18n'
import { useAuthStore } from '@/stores/auth'
import { apiMock, apiResult, testUser } from '@/test/apiMock'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { toast } from 'vue-sonner'
import TeamPage from './TeamPage.vue'

vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  api: (await import('@/test/apiMock')).apiMock,
}))
vi.mock('vue-sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }))

const me = { ...testUser, roles: ['TenantAdmin', 'User'] }
const colleague = '6a4b0f0e-0000-0000-0000-000000000002'

const team = () => ({
  tenant: { id: 't1', name: 'Acme', maxAccounts: 5, accounts: 2 },
  users: [
    { id: me.id, email: me.email, displayName: me.displayName, isTenantAdmin: true, isAdmin: false, accounts: 2, createdAt: '2026-10-01T10:00:00Z' },
    { id: colleague, email: 'carol@acme.test', displayName: null, isTenantAdmin: false, isAdmin: false, accounts: 0, createdAt: '2026-10-20T10:00:00Z' },
  ],
  invitations: [{ id: 'i1', email: null, createdAt: '2026-10-24T10:00:00Z', expiresAt: '2026-10-31T10:00:00Z' }],
})

async function mountTeam() {
  apiMock.GET.mockResolvedValue(apiResult(200, team()))
  const wrapper = mount(TeamPage, { global: { plugins: [i18n] }, attachTo: document.body })
  await flushPromises()
  return wrapper
}

describe('TeamPage', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.resetAllMocks()
    i18n.global.locale.value = 'en'
    useAuthStore().user = me
  })

  it('shows members, the mailbox usage and open invitations', async () => {
    const wrapper = await mountTeam()

    expect(wrapper.find('[data-testid="mailbox-usage"]').text()).toBe('2 of 5 mailboxes connected')
    expect(wrapper.find(`[data-testid="member-${me.email}"]`).text()).toContain('You')
    expect(wrapper.find('[data-testid="member-carol@acme.test"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="invitations"]').text()).toContain('Anyone with the link')
    // Nobody can remove themselves.
    expect(wrapper.find(`[aria-label="Remove ${me.email}"]`).exists()).toBe(false)
    expect(wrapper.find('[aria-label="Remove carol@acme.test"]').exists()).toBe(true)
  })

  it('shows the invitation link once after creating an invitation', async () => {
    const wrapper = await mountTeam()
    apiMock.POST.mockResolvedValue(
      apiResult(200, { invitation: team().invitations[0], url: 'https://mailnix.test/register?invite=abc' }),
    )

    await wrapper.find('#invite-email').setValue('dave@acme.test')
    await wrapper.find('[data-testid="create-invitation"]').trigger('submit')
    await flushPromises()

    expect(apiMock.POST).toHaveBeenCalledWith('/api/team/invitations', { body: { email: 'dave@acme.test' } })
    expect(wrapper.find<HTMLInputElement>('[data-testid="invitation-link"] input').element.value).toBe(
      'https://mailnix.test/register?invite=abc',
    )
  })

  it('explains why the last admin cannot step down', async () => {
    const wrapper = await mountTeam()
    apiMock.PUT.mockResolvedValue(apiResult(409, { title: 'Someone has to stay administrator', errorCode: 'LastTenantAdmin' }))

    await wrapper.find(`[data-testid="admin-${me.email}"]`).trigger('click')
    await flushPromises()

    expect(apiMock.PUT).toHaveBeenCalledWith('/api/team/users/{id}/admin', {
      params: { path: { id: me.id } },
      body: { isTenantAdmin: false },
    })
    expect(toast.error).toHaveBeenCalledWith(
      'Someone has to stay admin of the organization. Make another member admin first.',
    )
  })
})
