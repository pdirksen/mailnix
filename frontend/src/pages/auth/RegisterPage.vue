<script setup lang="ts">
import FormField from '@/components/common/FormField.vue'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { errorMessages } from '@/lib/errors'
import { toTypedSchema } from '@/lib/zodForm'
import { useAuthStore } from '@/stores/auth'
import { CircleAlertIcon, Loader2Icon } from '@lucide/vue'
import { useForm } from 'vee-validate'
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterLink, useRouter } from 'vue-router'
import { z } from 'zod'

/** Must match the server's password policy (Identity RequiredLength). */
const MIN_PASSWORD_LENGTH = 10

const { t } = useI18n()
const auth = useAuthStore()
const router = useRouter()

const schema = z
  .object({
    email: z.email({ error: () => t('validation.email') }).max(256, { error: () => t('validation.maxLength', { max: 256 }) }),
    displayName: z.string().max(200, { error: () => t('validation.maxLength', { max: 200 }) }),
    password: z
      .string()
      .min(MIN_PASSWORD_LENGTH, { error: () => t('validation.passwordLength', { min: MIN_PASSWORD_LENGTH }) })
      .max(256, { error: () => t('validation.maxLength', { max: 256 }) }),
    confirmPassword: z.string(),
  })
  .refine((values) => values.password === values.confirmPassword, {
    path: ['confirmPassword'],
    error: () => t('validation.passwordMismatch'),
  })

const { defineField, handleSubmit, errors, isSubmitting } = useForm({
  validationSchema: toTypedSchema(schema),
  initialValues: { email: '', displayName: '', password: '', confirmPassword: '' },
})
const [email, emailAttrs] = defineField('email')
const [displayName, displayNameAttrs] = defineField('displayName')
const [password, passwordAttrs] = defineField('password')
const [confirmPassword, confirmPasswordAttrs] = defineField('confirmPassword')

const failure = ref<string[]>([])

const onSubmit = handleSubmit(async ({ email, displayName, password }) => {
  failure.value = []
  try {
    await auth.register({ email, password, displayName: displayName.trim() || null })
    await router.replace({ name: 'dashboard' })
  } catch (error) {
    failure.value = errorMessages(error, t('common.unexpectedError'))
  }
})
</script>

<template>
  <Card>
    <CardHeader>
      <CardTitle class="text-xl">{{ $t('auth.registerTitle') }}</CardTitle>
      <CardDescription>{{ $t('auth.registerSubtitle') }}</CardDescription>
    </CardHeader>
    <form novalidate @submit="onSubmit">
      <CardContent class="grid gap-4">
        <Alert v-if="failure.length > 0" variant="destructive" data-testid="register-error">
          <CircleAlertIcon />
          <AlertTitle>{{ $t('auth.registerFailed') }}</AlertTitle>
          <AlertDescription>
            <p v-for="message in failure" :key="message">{{ message }}</p>
          </AlertDescription>
        </Alert>

        <FormField id="email" v-slot="aria" :label="$t('auth.email')" :error="errors.email">
          <Input id="email" v-model="email" v-bind="{ ...emailAttrs, ...aria }" type="email" autocomplete="email" />
        </FormField>
        <FormField
          id="display-name"
          v-slot="aria"
          :label="$t('auth.displayName')"
          :hint="$t('auth.displayNameHint')"
          :error="errors.displayName"
        >
          <Input id="display-name" v-model="displayName" v-bind="{ ...displayNameAttrs, ...aria }" autocomplete="name" />
        </FormField>
        <FormField id="password" v-slot="aria" :label="$t('auth.password')" :error="errors.password">
          <Input
            id="password"
            v-model="password"
            v-bind="{ ...passwordAttrs, ...aria }"
            type="password"
            autocomplete="new-password"
          />
        </FormField>
        <FormField
          id="confirm-password"
          v-slot="aria"
          :label="$t('auth.confirmPassword')"
          :error="errors.confirmPassword"
        >
          <Input
            id="confirm-password"
            v-model="confirmPassword"
            v-bind="{ ...confirmPasswordAttrs, ...aria }"
            type="password"
            autocomplete="new-password"
          />
        </FormField>
      </CardContent>
      <CardFooter class="mt-6 flex flex-col gap-4">
        <Button type="submit" class="w-full" :disabled="isSubmitting">
          <Loader2Icon v-if="isSubmitting" class="animate-spin" />
          {{ $t('auth.register') }}
        </Button>
        <p class="text-muted-foreground text-sm">
          {{ $t('auth.haveAccount') }}
          <RouterLink :to="{ name: 'login' }" class="text-foreground underline underline-offset-4">
            {{ $t('auth.login') }}
          </RouterLink>
        </p>
      </CardFooter>
    </form>
  </Card>
</template>
