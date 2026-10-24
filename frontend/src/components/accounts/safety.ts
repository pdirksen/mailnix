import { z } from 'zod'

export interface SafetySettings {
  blastRadiusLimit: number
  /** null = never purge (default) */
  purgeTrashAfterDays: number | null
}

type Translate = (key: string, named?: Record<string, unknown>) => string

/** Matches the API's ranges for BlastRadiusLimit and PurgeTrashAfterDays. */
export function safetySchema(t: Translate) {
  const range = (min: number, max: number) =>
    z.number({ error: () => t('validation.numberRange', { min, max }) }).int().min(min, { error: () => t('validation.numberRange', { min, max }) }).max(max, {
      error: () => t('validation.numberRange', { min, max }),
    })
  return z.object({
    blastRadiusLimit: range(1, 100_000),
    purgeTrashAfterDays: range(1, 3650).nullable(),
  })
}
