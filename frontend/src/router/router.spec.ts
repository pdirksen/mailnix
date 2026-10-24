import { describe, expect, it } from 'vitest'
import { createMemoryHistory, createRouter } from 'vue-router'
import { routes } from '.'

describe('routes', () => {
  const router = createRouter({ history: createMemoryHistory(), routes })

  it.each([
    ['/', 'dashboard'],
    ['/login', 'login'],
    ['/register', 'register'],
    ['/accounts', 'accounts'],
    ['/accounts/new', 'account-new'],
    ['/accounts/123', 'account-detail'],
    ['/lists', 'lists'],
    ['/lists/123', 'list-detail'],
    ['/does/not/exist', 'not-found'],
  ])('%s resolves to %s', (path, name) => {
    expect(router.resolve(path).name).toBe(name)
  })

  it('protects the dashboard', () => {
    expect(router.resolve('/').matched.some((r) => r.meta.requiresAuth)).toBe(true)
  })
})
