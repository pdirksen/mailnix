import { createI18n } from 'vue-i18n'
import de from './de.json'
import en from './en.json'

export type MessageSchema = typeof en

export const SUPPORTED_LOCALES = ['en', 'de'] as const
export type Locale = (typeof SUPPORTED_LOCALES)[number]

export const LOCALE_NAMES: Record<Locale, string> = { en: 'English', de: 'Deutsch' }

export function isLocale(value: unknown): value is Locale {
  return typeof value === 'string' && (SUPPORTED_LOCALES as readonly string[]).includes(value)
}

/** Best match from the browser's preferred languages, falling back to English. */
export function detectLocale(languages: readonly string[] = globalThis.navigator?.languages ?? []): Locale {
  for (const language of languages) {
    const base = language.toLowerCase().split('-')[0]
    if (isLocale(base)) return base
  }
  return 'en'
}

// Typed against the English messages, so a missing German key is a compile error.
const messages: Record<Locale, MessageSchema> = { en, de }

export const i18n = createI18n({
  legacy: false,
  locale: detectLocale(),
  fallbackLocale: 'en',
  messages,
})
