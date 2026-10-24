import { describe, expect, it } from 'vitest'
import { z } from 'zod'
import { toTypedSchema } from './zodForm'

describe('toTypedSchema', () => {
  const schema = toTypedSchema(
    z.object({
      email: z.email({ error: () => 'bad email' }),
      items: z.array(z.object({ name: z.string().min(1, { error: () => 'required' }) })),
    }),
  )

  it('returns parsed values when valid', async () => {
    const result = await schema.parse({ email: 'a@example.com', items: [{ name: 'x' }] })
    expect(result).toEqual({ value: { email: 'a@example.com', items: [{ name: 'x' }] }, errors: [] })
  })

  it('maps issues to vee-validate paths', async () => {
    const result = await schema.parse({ email: 'nope', items: [{ name: 'ok' }, { name: '' }] })
    expect(result.value).toBeUndefined()
    expect(result.errors).toEqual([
      { path: 'email', errors: ['bad email'] },
      { path: 'items[1].name', errors: ['required'] },
    ])
  })
})
