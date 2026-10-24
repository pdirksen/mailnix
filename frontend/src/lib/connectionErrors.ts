import { ApiError } from '@/api/client'
import { useI18n } from 'vue-i18n'

export interface DescribedError {
  /** Translated, user-facing explanation. */
  message: string
  /** Technical detail from the server (e.g. the TLS failure reason), if it adds anything. */
  detail?: string
}

/** Translates connection error codes from the API; falls back to the server's message. */
export function useConnectionErrors() {
  const { t, te } = useI18n()

  function describe(code: string | null | undefined, serverMessage?: string | null): DescribedError {
    const key = `connectionErrors.${code}`
    if (code && te(key)) {
      return { message: t(key), detail: serverMessage ?? undefined }
    }
    return { message: serverMessage || t('connectionErrors.Unknown') }
  }

  /** For failed requests: connection problems get a translated text, everything else the server's messages. */
  function describeRequestError(error: unknown): DescribedError {
    if (error instanceof ApiError) {
      return error.code ? describe(error.code, error.message) : { message: error.messages.join(' ') }
    }
    return { message: t('common.unexpectedError') }
  }

  return { describe, describeRequestError }
}
