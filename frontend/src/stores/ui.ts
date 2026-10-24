import { i18n, isLocale, type Locale } from '@/i18n'
import { useColorMode, useStorage } from '@vueuse/core'
import { defineStore } from 'pinia'
import { watch } from 'vue'

export type ThemePreference = 'light' | 'dark' | 'auto'

export const useUiStore = defineStore('ui', () => {
  // Adds/removes the `dark` class on <html>, which the theme tokens in main.css key off.
  const colorMode = useColorMode({ storageKey: 'mailnix-theme', emitAuto: true })

  const storedLocale = useStorage<string>('mailnix-locale', i18n.global.locale.value)
  if (!isLocale(storedLocale.value)) storedLocale.value = i18n.global.locale.value

  watch(
    storedLocale,
    (locale) => {
      if (!isLocale(locale)) return
      i18n.global.locale.value = locale
      document.documentElement.lang = locale
    },
    { immediate: true },
  )

  function setTheme(theme: ThemePreference) {
    colorMode.value = theme
  }

  function setLocale(locale: Locale) {
    storedLocale.value = locale
  }

  return { theme: colorMode, locale: storedLocale, setTheme, setLocale }
})
