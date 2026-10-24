import { ApiError } from '@/api/client'

/** Messages to show for a failed request; falls back to a generic text for network/unknown errors. */
export function errorMessages(error: unknown, fallback: string): string[] {
  return error instanceof ApiError ? error.messages : [fallback]
}
