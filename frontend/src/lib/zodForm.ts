import type { TypedSchema, TypedSchemaError } from 'vee-validate'
import type { z } from 'zod'
import { type MaybeRefOrGetter, ref, toValue } from 'vue'

/** vee-validate field path: `items[0].name` */
function toFieldPath(path: readonly PropertyKey[]): string {
  return path.reduce<string>((acc, segment) => {
    if (typeof segment === 'number') return `${acc}[${segment}]`
    return acc ? `${acc}.${String(segment)}` : String(segment)
  }, '')
}

/**
 * Adapts a Zod 4 schema to vee-validate. (`@vee-validate/zod` only supports Zod 3, and vee-validate does not
 * run Zod 4 schemas on its own even though they type-check.)
 */
export function toTypedSchema<TSchema extends z.ZodType>(schema: TSchema): TypedSchema<z.input<TSchema>, z.output<TSchema>> {
  return {
    __type: 'VVTypedSchema',
    async parse(values) {
      const result = await schema.safeParseAsync(values)
      if (result.success) return { value: result.data, errors: [] }

      const byPath = new Map<string, string[]>()
      for (const issue of result.error.issues) {
        const path = toFieldPath(issue.path)
        byPath.set(path, [...(byPath.get(path) ?? []), issue.message])
      }
      return { errors: [...byPath].map(([path, errors]): TypedSchemaError => ({ path, errors })) }
    },
  }
}

/**
 * Minimal schema validation for forms spread over several components (e.g. wizard steps):
 * `validate(values)` returns the parsed values or null and fills `errors` (first message per field path).
 */
export function useSchemaValidation<TSchema extends z.ZodType>(schema: MaybeRefOrGetter<TSchema>) {
  const errors = ref<Record<string, string>>({})

  function validate(values: unknown): z.output<TSchema> | null {
    const result = toValue(schema).safeParse(values)
    if (result.success) {
      errors.value = {}
      return result.data
    }
    const next: Record<string, string> = {}
    for (const issue of result.error.issues) {
      next[toFieldPath(issue.path)] ??= issue.message
    }
    errors.value = next
    return null
  }

  return { errors, validate }
}
