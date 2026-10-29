import { i18n } from '@/i18n'
import type { Account } from '@/stores/accounts'
import { useUiStore } from '@/stores/ui'
import { apiMock, apiResult } from '@/test/apiMock'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import AccountSettingsForm from './AccountSettingsForm.vue'

vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  api: (await import('@/test/apiMock')).apiMock,
}))
vi.mock('vue-sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }))

const account: Account = {
  id: 'account-1',
  name: 'Work',
  provider: 'Imap',
  host: 'imap.example.com',
  port: 993,
  security: 'SslOnConnect',
  username: 'me@example.com',
  enabled: true,
  status: 'Ok',
  lastErrorCode: null,
  lastError: null,
  lastCheckedAt: null,
  watchedFolders: ['INBOX'],
  specialFolders: { sent: null, drafts: null, trash: 'Trash', junk: null, archive: null },
  capabilities: [],
  blastRadiusLimit: 200,
  purgeTrashAfterDays: null,
  lastPurgeAt: null,
  createdAt: '2026-10-20T10:00:00Z',
}

async function mountForm() {
  apiMock.GET.mockResolvedValue(apiResult(200, { delimiter: '/', folders: [], specialFolders: account.specialFolders }))
  const wrapper = mount(AccountSettingsForm, { props: { account }, global: { plugins: [i18n] }, attachTo: document.body })
  await flushPromises()
  return wrapper
}

describe('AccountSettingsForm', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.resetAllMocks()
    useUiStore().setLocale('en')
  })

  it('keeps the stored password when only the folders or the name change', async () => {
    const wrapper = await mountForm()
    apiMock.PUT.mockResolvedValue(apiResult(200, { ...account, name: 'Office' }))

    await wrapper.find('#account-name').setValue('Office')
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(apiMock.PUT).toHaveBeenCalledWith('/api/accounts/{id}', expect.objectContaining({ body: expect.objectContaining({ name: 'Office', password: null }) }))
  })

  it('asks for the password again when the server changes', async () => {
    const wrapper = await mountForm()
    expect(wrapper.text()).toContain('Leave empty to keep the current password.')

    await wrapper.find('#account-host').setValue('mail.attacker.example')
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(apiMock.PUT).not.toHaveBeenCalled()
    expect(wrapper.find('#account-password').attributes('aria-invalid')).toBe('true')
    expect(wrapper.text()).toContain('Enter the password again')

    apiMock.PUT.mockResolvedValue(apiResult(200, { ...account, host: 'mail.attacker.example' }))
    await wrapper.find('#account-password').setValue('the password')
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(apiMock.PUT).toHaveBeenCalledWith(
      '/api/accounts/{id}',
      expect.objectContaining({ body: expect.objectContaining({ host: 'mail.attacker.example', password: 'the password' }) }),
    )
  })
})
