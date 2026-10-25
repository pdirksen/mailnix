import { apiMock, apiResult, testUser } from '@/test/apiMock'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createMemoryHistory, createRouter } from 'vue-router'
import { router as appRouter, routes } from '.'

vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  api: (await import('@/test/apiMock')).apiMock,
}))

describe('routes', () => {
  const router = createRouter({ history: createMemoryHistory(), routes })

  it.each([
    ['/', 'dashboard'],
    ['/login', 'login'],
    ['/register', 'register'],
    ['/accounts', 'accounts'],
    ['/accounts/new', 'account-new'],
    ['/accounts/123', 'account-detail'],
    ['/rules', 'rules'],
    ['/rules/new', 'rule-new'],
    ['/rules/123', 'rule-edit'],
    ['/lists', 'lists'],
    ['/lists/123', 'list-detail'],
    ['/admin/team', 'admin-team'],
    ['/admin/system', 'admin-system'],
    ['/does/not/exist', 'not-found'],
  ])('%s resolves to %s', (path, name) => {
    expect(router.resolve(path).name).toBe(name)
  })

  it('protects the dashboard', () => {
    expect(router.resolve('/').matched.some((r) => r.meta.requiresAuth)).toBe(true)
  })
})

describe('navigation guard', () => {
  /** The server's answers for the session check the guard makes. */
  function serve(roles: string[] | null, registrationOpen = false) {
    apiMock.GET.mockImplementation((path: string) =>
      Promise.resolve(
        path === '/api/auth/me'
          ? roles
            ? apiResult(200, { ...testUser, roles })
            : apiResult(401)
          : apiResult(200, { registrationOpen, multiTenant: true }),
      ),
    )
  }

  beforeEach(async () => {
    setActivePinia(createPinia())
    vi.resetAllMocks()
    // Start every test signed out on a neutral page.
    serve(null)
    await appRouter.push('/login')
  })

  it('lets invited visitors register although signup is closed', async () => {
    await appRouter.push('/register')
    expect(appRouter.currentRoute.value.name).toBe('login')

    await appRouter.push('/register?invite=abc')
    expect(appRouter.currentRoute.value.name).toBe('register')
  })

  it.each([
    [['User'], '/admin/team', 'dashboard'],
    [['TenantAdmin', 'User'], '/admin/team', 'admin-team'],
    [['TenantAdmin', 'User'], '/admin/system', 'dashboard'],
    [['Admin', 'TenantAdmin', 'User'], '/admin/system', 'admin-system'],
  ])('with roles %j, %s leads to %s', async (roles, path, name) => {
    setActivePinia(createPinia())
    serve(roles)

    await appRouter.push(path)

    expect(appRouter.currentRoute.value.name).toBe(name)
  })
})
