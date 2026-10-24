import type { ImapSecurity } from '@/stores/accounts'
import { z } from 'zod'

export interface ServerSettings {
  name: string
  host: string
  port: number
  security: ImapSecurity
  username: string
  password: string
}

export function emptyServerSettings(): ServerSettings {
  return { name: '', host: '', port: 993, security: 'SslOnConnect', username: '', password: '' }
}

type Translate = (key: string, named?: Record<string, unknown>) => string

/** Mirrors the API's validation; `passwordRequired` is false when editing (empty = keep the stored one). */
export function serverSettingsSchema(t: Translate, { passwordRequired }: { passwordRequired: boolean }) {
  const required = () => t('validation.required')
  return z.object({
    name: z.string().trim().min(1, { error: required }).max(200, { error: () => t('validation.maxLength', { max: 200 }) }),
    host: z.string().trim().min(1, { error: required }).max(253, { error: () => t('validation.maxLength', { max: 253 }) }),
    port: z.number({ error: () => t('validation.portRange') }).int().min(1, { error: () => t('validation.portRange') }).max(65535, {
      error: () => t('validation.portRange'),
    }),
    security: z.enum(['SslOnConnect', 'StartTls', 'None']),
    username: z.string().trim().min(1, { error: required }).max(320, { error: () => t('validation.maxLength', { max: 320 }) }),
    password: passwordRequired
      ? z.string().min(1, { error: required }).max(1024)
      : z.string().max(1024),
  })
}
