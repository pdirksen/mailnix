import { ApiError } from '@/api/client'
import { apiMock, apiResult, testUser } from '@/test/apiMock'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { useAuthStore } from './auth'

vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  api: (await import('@/test/apiMock')).apiMock,
}))

describe('auth store', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.resetAllMocks()
  })

  it('initializes the session once', async () => {
    apiMock.GET.mockImplementation(async (path: string) =>
      path === '/api/auth/me' ? apiResult(200, testUser) : apiResult(200, { registrationOpen: true }),
    )
    const auth = useAuthStore()

    await Promise.all([auth.initialize(), auth.initialize()])

    expect(apiMock.GET).toHaveBeenCalledTimes(2) // me + config, not repeated
    expect(auth.isAuthenticated).toBe(true)
    expect(auth.displayName).toBe('Test User')
    expect(auth.registrationOpen).toBe(true)
  })

  it('treats 401 from /me as signed out', async () => {
    apiMock.GET.mockImplementation(async (path: string) =>
      path === '/api/auth/me' ? apiResult(401) : apiResult(200, { registrationOpen: false }),
    )
    const auth = useAuthStore()

    await auth.initialize()

    expect(auth.isAuthenticated).toBe(false)
    expect(auth.initialized).toBe(true)
  })

  it('treats an unreachable backend as signed out', async () => {
    apiMock.GET.mockRejectedValue(new TypeError('Failed to fetch'))
    const auth = useAuthStore()

    await auth.initialize()

    expect(auth.isAuthenticated).toBe(false)
    expect(auth.initialized).toBe(true)
  })

  it('stores the user after login and recognizes admins', async () => {
    apiMock.POST.mockResolvedValue(apiResult(200, { ...testUser, roles: ['Admin', 'User'] }))
    const auth = useAuthStore()

    await auth.login({ email: testUser.email, password: 'secret password', rememberMe: false })

    expect(auth.user?.email).toBe(testUser.email)
    expect(auth.isAdmin).toBe(true)
  })

  it('throws ApiError with the server message when login fails', async () => {
    apiMock.POST.mockResolvedValue(apiResult(401, { title: 'Invalid email or password.' }))
    const auth = useAuthStore()

    const error = await auth.login({ email: testUser.email, password: 'wrong', rememberMe: false }).catch((e) => e)

    expect(error).toBeInstanceOf(ApiError)
    expect((error as ApiError).messages).toEqual(['Invalid email or password.'])
    expect(auth.isAuthenticated).toBe(false)
  })

  it('surfaces validation messages from registration', async () => {
    apiMock.POST.mockResolvedValue(
      apiResult(400, { title: 'One or more validation errors occurred.', errors: { DuplicateEmail: ['Email is taken.'] } }),
    )
    const auth = useAuthStore()

    const error = await auth.register({ email: testUser.email, password: 'long enough pw' }).catch((e) => e)

    expect((error as ApiError).messages).toEqual(['Email is taken.'])
  })

  it('clears the user on logout even if the request fails', async () => {
    apiMock.POST.mockResolvedValueOnce(apiResult(200, testUser)).mockRejectedValueOnce(new TypeError('offline'))
    const auth = useAuthStore()
    await auth.login({ email: testUser.email, password: 'secret password', rememberMe: false })

    await auth.logout().catch(() => {})

    expect(auth.isAuthenticated).toBe(false)
  })
})
