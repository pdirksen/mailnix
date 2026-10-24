import createClient, { type Middleware } from 'openapi-fetch'
import type { components, paths } from './schema'

export type Schemas = components['schemas']

/** Sent on every request; the API rejects state-changing requests without it (CSRF protection). */
export const CSRF_HEADER = { 'X-Requested-With': 'mailnix' } as const

/** RFC 9457 problem details as returned by the API. */
export interface ProblemDetails {
  title?: string
  status?: number
  detail?: string
  errors?: Record<string, string[]>
}

export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly problem: ProblemDetails,
  ) {
    super(problem.title ?? `Request failed with status ${status}`)
    this.name = 'ApiError'
  }

  /** All human-readable messages: validation errors if present, otherwise the title. */
  get messages(): string[] {
    const validation = Object.values(this.problem.errors ?? {}).flat()
    return validation.length > 0 ? validation : [this.message]
  }
}

let unauthorizedHandler: (() => void) | undefined

/** Called when an authenticated request comes back 401 (session expired or account removed). */
export function setUnauthorizedHandler(handler: () => void) {
  unauthorizedHandler = handler
}

const unauthorizedMiddleware: Middleware = {
  onResponse({ request, response }) {
    // Auth endpoints answer 401 as part of their normal flow (e.g. "not signed in", "wrong password").
    if (response.status === 401 && !new URL(request.url).pathname.startsWith('/api/auth/')) {
      unauthorizedHandler?.()
    }
    return response
  },
}

export const api = createClient<paths>({
  baseUrl: globalThis.location?.origin ?? '',
  headers: CSRF_HEADER,
  credentials: 'same-origin',
})
api.use(unauthorizedMiddleware)

/** Unwraps an openapi-fetch result, throwing {@link ApiError} for non-2xx responses. */
export function unwrap<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (result.error !== undefined || !result.response.ok) {
    const problem = (typeof result.error === 'object' && result.error !== null ? result.error : {}) as ProblemDetails
    throw new ApiError(result.response.status, problem)
  }
  return result.data as T
}
