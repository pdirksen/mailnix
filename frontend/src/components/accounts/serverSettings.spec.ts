import { i18n } from '@/i18n'
import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import { ref } from 'vue'
import ServerSettingsFields from './ServerSettingsFields.vue'
import { emptyServerSettings, type ServerSettings, serverSettingsSchema } from './serverSettings'

const t = i18n.global.t as (key: string, named?: Record<string, unknown>) => string

describe('serverSettingsSchema', () => {
  const valid: ServerSettings = { name: 'Work', host: 'imap.example.com', port: 993, security: 'SslOnConnect', username: 'me', password: 'pw' }

  it('accepts valid settings and trims text', () => {
    const result = serverSettingsSchema(t, { passwordRequired: true }).safeParse({ ...valid, host: ' imap.example.com ' })
    expect(result.success && result.data.host).toBe('imap.example.com')
  })

  it('rejects out-of-range ports and missing fields', () => {
    const result = serverSettingsSchema(t, { passwordRequired: true }).safeParse({ ...valid, port: 70000, host: '', password: '' })
    expect(result.success).toBe(false)
    expect(result.error!.issues.map((i) => i.path[0]).sort()).toEqual(['host', 'password', 'port'])
  })

  it('lets the password stay empty when editing', () => {
    expect(serverSettingsSchema(t, { passwordRequired: false }).safeParse({ ...valid, password: '' }).success).toBe(true)
  })
})

describe('ServerSettingsFields', () => {
  function mountFields(settings: ServerSettings) {
    const model = ref(settings)
    const wrapper = mount(ServerSettingsFields, {
      props: { modelValue: model.value, errors: {}, 'onUpdate:modelValue': (v: ServerSettings) => (model.value = v) },
      global: { plugins: [i18n] },
      attachTo: document.body,
    })
    return { wrapper, model }
  }

  it('follows the standard port when switching encryption', async () => {
    const { wrapper, model } = mountFields(emptyServerSettings())

    await wrapper.find('#security-StartTls').trigger('click')

    expect(model.value.security).toBe('StartTls')
    expect(model.value.port).toBe(143)
  })

  it('keeps a custom port when switching encryption', async () => {
    const { wrapper, model } = mountFields({ ...emptyServerSettings(), port: 10993 })

    await wrapper.find('#security-StartTls').trigger('click')

    expect(model.value.port).toBe(10993)
  })
})
