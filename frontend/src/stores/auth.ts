import { api, ApiError, type Schemas, unwrap } from '@/api/client'
import { isLocale } from '@/i18n'
import { browserTimeZone } from '@/lib/timeZones'
import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { useUiStore } from './ui'

export type CurrentUser = Schemas['MeResponse']
export type LoginInput = Schemas['LoginRequest']
export type RegisterInput = Omit<Schemas['RegisterRequest'], 'timeZone' | 'language'>
export type TwoFactorInput = Schemas['TwoFactorLoginRequest']

/** Signed in, or the password was right and a code from the authenticator app is needed next. */
export type LoginResult = 'signedIn' | 'twoFactorRequired'

export const useAuthStore = defineStore('auth', () => {
  const user = ref<CurrentUser | null>(null)
  const registrationOpen = ref(false)
  /** SaaS: every signup founds its own organization. */
  const multiTenant = ref(false)
  const initialized = ref(false)

  const isAuthenticated = computed(() => user.value !== null)
  const isAdmin = computed(() => user.value?.roles.includes('Admin') ?? false)
  /** Manages the users of their organization (installation admins may too). */
  const isTenantAdmin = computed(() => isAdmin.value || (user.value?.roles.includes('TenantAdmin') ?? false))
  const displayName = computed(() => user.value?.displayName || user.value?.email || '')

  let initializing: Promise<void> | undefined

  /** Loads the current session once; safe to call from every navigation. */
  function initialize(): Promise<void> {
    initializing ??= (async () => {
      try {
        const [me, config] = await Promise.all([api.GET('/api/auth/me'), api.GET('/api/auth/config')])
        setUser(me.response.ok ? (me.data ?? null) : null)
        registrationOpen.value = config.data?.registrationOpen ?? false
        multiTenant.value = config.data?.multiTenant ?? false
      } catch {
        // Backend unreachable: treat as signed out; the login page reports errors on submit.
        user.value = null
      } finally {
        initialized.value = true
      }
    })()
    return initializing
  }

  /** The signed-in user (or none); a language they chose follows them to every browser. */
  function setUser(me: CurrentUser | null) {
    user.value = me
    if (isLocale(me?.language)) useUiStore().setLocale(me.language)
  }

  async function login(input: LoginInput): Promise<LoginResult> {
    try {
      setUser(unwrap(await api.POST('/api/auth/login', { body: input })))
      return 'signedIn'
    } catch (error) {
      if (error instanceof ApiError && error.code === 'TwoFactorRequired') return 'twoFactorRequired'
      throw error
    }
  }

  async function loginTwoFactor(input: TwoFactorInput) {
    setUser(unwrap(await api.POST('/api/auth/login/two-factor', { body: input })))
  }

  async function register(input: RegisterInput) {
    // The browser's time zone and the current language, so dates and texts fit from the start.
    const body = { ...input, timeZone: browserTimeZone(), language: useUiStore().locale }
    setUser(unwrap(await api.POST('/api/auth/register', { body })))
    registrationOpen.value = (await api.GET('/api/auth/config')).data?.registrationOpen ?? false
  }

  async function logout() {
    try {
      await api.POST('/api/auth/logout')
    } finally {
      user.value = null
    }
  }

  /** Forget the session locally, e.g. after the server answered 401. */
  function clear() {
    user.value = null
  }

  return {
    user,
    registrationOpen,
    multiTenant,
    initialized,
    isAuthenticated,
    isAdmin,
    isTenantAdmin,
    displayName,
    initialize,
    setUser,
    login,
    loginTwoFactor,
    register,
    logout,
    clear,
  }
})
