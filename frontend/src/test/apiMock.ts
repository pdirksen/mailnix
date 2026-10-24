import { vi } from 'vitest'

/** Builds an openapi-fetch style result. */
export function apiResult<T>(status: number, body?: T) {
  const ok = status >= 200 && status < 300
  return {
    data: ok ? body : undefined,
    error: ok ? undefined : (body ?? {}),
    response: new Response(null, { status }),
  }
}

/** Replacement for the `api` client; use with `vi.mock('@/api/client', …)`. */
export const apiMock = {
  GET: vi.fn(),
  POST: vi.fn(),
  PUT: vi.fn(),
  DELETE: vi.fn(),
}

export const testUser = {
  id: '6a4b0f0e-0000-0000-0000-000000000001',
  email: 'user@example.com',
  displayName: 'Test User',
  roles: ['User'],
  timeZone: 'UTC',
  language: 'en',
}
