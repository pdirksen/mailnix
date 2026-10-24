import { api, type Schemas, unwrap } from '@/api/client'
import { defineStore } from 'pinia'
import { computed, ref } from 'vue'

export type CurrentUser = Schemas['MeResponse']
export type LoginInput = Schemas['LoginRequest']
export type RegisterInput = Schemas['RegisterRequest']

export const useAuthStore = defineStore('auth', () => {
  const user = ref<CurrentUser | null>(null)
  const registrationOpen = ref(false)
  const initialized = ref(false)

  const isAuthenticated = computed(() => user.value !== null)
  const isAdmin = computed(() => user.value?.roles.includes('Admin') ?? false)
  const displayName = computed(() => user.value?.displayName || user.value?.email || '')

  let initializing: Promise<void> | undefined

  /** Loads the current session once; safe to call from every navigation. */
  function initialize(): Promise<void> {
    initializing ??= (async () => {
      try {
        const [me, config] = await Promise.all([api.GET('/api/auth/me'), api.GET('/api/auth/config')])
        user.value = me.response.ok ? (me.data ?? null) : null
        registrationOpen.value = config.data?.registrationOpen ?? false
      } catch {
        // Backend unreachable: treat as signed out; the login page reports errors on submit.
        user.value = null
      } finally {
        initialized.value = true
      }
    })()
    return initializing
  }

  async function login(input: LoginInput) {
    user.value = unwrap(await api.POST('/api/auth/login', { body: input }))
  }

  async function register(input: RegisterInput) {
    user.value = unwrap(await api.POST('/api/auth/register', { body: input }))
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

  return { user, registrationOpen, initialized, isAuthenticated, isAdmin, displayName, initialize, login, register, logout, clear }
})
