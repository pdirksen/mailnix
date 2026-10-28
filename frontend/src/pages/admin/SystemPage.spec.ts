import { i18n } from '@/i18n'
import { apiMock, apiResult } from '@/test/apiMock'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import SystemPage from './SystemPage.vue'

vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  api: (await import('@/test/apiMock')).apiMock,
}))
vi.mock('vue-sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }))

const system = {
  version: '1.4.0',
  mode: 'Api',
  multiTenant: true,
  workers: [
    { id: 'host-a-1-abc', host: 'host-a', startedAt: '2026-10-25T08:00:00Z', lastSeenAt: new Date().toISOString(), leases: 3, alive: true },
    { id: 'host-b-2-def', host: 'host-b', startedAt: '2026-10-25T08:00:00Z', lastSeenAt: '2026-10-25T08:01:00Z', leases: 0, alive: false },
  ],
  accounts: 4,
  unleasedAccounts: 1,
  queuedRuns: 2,
  awaitingConfirmation: 0,
  tenants: [{ id: 't1', name: 'Acme', users: 2, accounts: 4, maxAccounts: null, createdAt: '2026-10-01T10:00:00Z' }],
  email: { configured: true, from: 'mailnix@example.org', publicUrl: 'https://mail.example.org' },
}

describe('SystemPage', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.resetAllMocks()
    i18n.global.locale.value = 'en'
  })

  it('shows the workers and saves a mailbox limit', async () => {
    apiMock.GET.mockResolvedValue(apiResult(200, system))
    apiMock.PUT.mockResolvedValue(apiResult(204))
    const wrapper = mount(SystemPage, { global: { plugins: [i18n] }, attachTo: document.body })
    await flushPromises()

    expect(wrapper.find('[data-testid="run-mode"]').text()).toBe('API process')
    expect(wrapper.find('[data-testid="version"]').text()).toBe('Version 1.4.0')
    expect(wrapper.find('[data-testid="kpi-unleased"]').text()).toContain('1')
    const workers = wrapper.find('[data-testid="workers"]').text()
    expect(workers).toContain('Running')
    expect(workers).toContain('Not responding')

    const limit = wrapper.find('[aria-label="Mailbox limit for Acme"]')
    await limit.setValue('0')
    expect(wrapper.find('[data-testid="tenant-Acme"] button').attributes('disabled')).toBeDefined()
    await limit.setValue('25')
    await wrapper.find('[data-testid="tenant-Acme"] form').trigger('submit')
    await flushPromises()

    expect(apiMock.PUT).toHaveBeenCalledWith('/api/system/tenants/{id}', { params: { path: { id: 't1' } }, body: { maxAccounts: 25 } })
  })

  it('sends a test email and shows why it failed', async () => {
    apiMock.GET.mockResolvedValue(apiResult(200, system))
    apiMock.POST.mockResolvedValue(
      apiResult(502, { title: 'The test email could not be sent.', detail: 'Sending through smtp.example.org failed: 535', errorCode: 'EmailFailed' }),
    )
    const wrapper = mount(SystemPage, { global: { plugins: [i18n] }, attachTo: document.body })
    await flushPromises()
    expect(wrapper.find('[data-testid="email-status"]').text()).toContain('Sends as mailnix@example.org')

    await wrapper.find('[data-testid="send-test-email"]').trigger('click')
    await flushPromises()

    expect(apiMock.POST).toHaveBeenCalledWith('/api/system/test-email')
    expect(wrapper.find('[data-testid="test-email-error"]').text()).toBe('Sending through smtp.example.org failed: 535')
  })
})
