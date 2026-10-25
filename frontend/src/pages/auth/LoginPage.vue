<script setup lang="ts">
import FormField from '@/components/common/FormField.vue'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { ApiError } from '@/api/client'
import { errorMessages } from '@/lib/errors'
import { toTypedSchema } from '@/lib/zodForm'
import { safeRedirect } from '@/router/safeRedirect'
import { useAuthStore } from '@/stores/auth'
import { CircleAlertIcon, Loader2Icon, SmartphoneIcon } from '@lucide/vue'
import { useForm } from 'vee-validate'
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterLink, useRoute, useRouter } from 'vue-router'
import { z } from 'zod'

const { t, te } = useI18n()
const auth = useAuthStore()
const route = useRoute()
const router = useRouter()

const schema = z.object({
  email: z.email({ error: () => t('validation.email') }),
  password: z.string({ error: () => t('validation.required') }).min(1, { error: () => t('validation.required') }),
  rememberMe: z.boolean(),
})

const { defineField, handleSubmit, errors, isSubmitting } = useForm({
  validationSchema: toTypedSchema(schema),
  initialValues: { email: '', password: '', rememberMe: false },
})
const [email, emailAttrs] = defineField('email')
const [password, passwordAttrs] = defineField('password')
const [rememberMe] = defineField('rememberMe')

const failure = ref<string[]>([])

/** After the password: a code from the authenticator app (or a recovery code) when two-factor sign-in is on. */
const step = ref<'password' | 'code'>('password')
const code = ref('')
const useRecoveryCode = ref(false)
const verifying = ref(false)

function describe(error: unknown) {
  const key = `auth.errors.${error instanceof ApiError ? error.code : ''}`
  return te(key) ? [t(key)] : errorMessages(error, t('common.unexpectedError'))
}

const onSubmit = handleSubmit(async (values) => {
  failure.value = []
  try {
    if ((await auth.login(values)) === 'twoFactorRequired') {
      step.value = 'code'
      code.value = ''
      return
    }
    await router.replace(safeRedirect(route.query.redirect))
  } catch (error) {
    failure.value = describe(error)
  }
})

async function verifyCode() {
  if (!code.value.trim()) {
    failure.value = [t('validation.required')]
    return
  }
  failure.value = []
  verifying.value = true
  try {
    await auth.loginTwoFactor({ code: code.value, isRecoveryCode: useRecoveryCode.value, rememberMe: rememberMe.value ?? false })
    await router.replace(safeRedirect(route.query.redirect))
  } catch (error) {
    failure.value = describe(error)
    // The password step has to be repeated once its short-lived confirmation is gone.
    if (error instanceof ApiError && error.code === 'TwoFactorExpired') step.value = 'password'
  } finally {
    verifying.value = false
  }
}

function backToPassword() {
  step.value = 'password'
  failure.value = []
  password.value = ''
}
</script>

<template>
  <Card v-if="step === 'code'">
    <CardHeader>
      <span class="bg-primary/10 text-primary mb-2 flex size-10 items-center justify-center rounded-full">
        <SmartphoneIcon class="size-5" />
      </span>
      <CardTitle class="text-xl">{{ $t('auth.twoFactorTitle') }}</CardTitle>
      <CardDescription>{{ useRecoveryCode ? $t('auth.recoveryCodeHint') : $t('auth.twoFactorHint') }}</CardDescription>
    </CardHeader>
    <form novalidate @submit.prevent="verifyCode">
      <CardContent class="grid gap-4">
        <Alert v-if="failure.length > 0" variant="destructive" data-testid="login-error">
          <CircleAlertIcon />
          <AlertDescription>
            <p v-for="message in failure" :key="message">{{ message }}</p>
          </AlertDescription>
        </Alert>
        <div class="grid gap-2">
          <Label for="login-code">{{ useRecoveryCode ? $t('auth.recoveryCode') : $t('auth.authenticatorCode') }}</Label>
          <Input
            id="login-code"
            v-model="code"
            :inputmode="useRecoveryCode ? 'text' : 'numeric'"
            :autocomplete="useRecoveryCode ? 'off' : 'one-time-code'"
            :placeholder="useRecoveryCode ? 'ABCDE-12345' : '123 456'"
            class="h-12 text-center font-mono text-xl tracking-[0.25em]"
            autofocus
          />
        </div>
        <button
          type="button"
          class="text-muted-foreground justify-self-start text-sm underline underline-offset-4"
          @click="useRecoveryCode = !useRecoveryCode; code = ''; failure = []"
        >
          {{ useRecoveryCode ? $t('auth.useAuthenticator') : $t('auth.useRecoveryCode') }}
        </button>
      </CardContent>
      <CardFooter class="mt-6 flex flex-col gap-3">
        <Button type="submit" class="w-full" :disabled="verifying" data-testid="verify-code">
          <Loader2Icon v-if="verifying" class="animate-spin" />
          {{ $t('auth.verify') }}
        </Button>
        <Button type="button" variant="ghost" class="w-full" @click="backToPassword">{{ $t('auth.backToPassword') }}</Button>
      </CardFooter>
    </form>
  </Card>

  <Card v-else>
    <CardHeader>
      <CardTitle class="text-xl">{{ $t('auth.loginTitle') }}</CardTitle>
      <CardDescription>{{ $t('auth.loginSubtitle') }}</CardDescription>
    </CardHeader>
    <form novalidate @submit="onSubmit">
      <CardContent class="grid gap-4">
        <Alert v-if="route.query.expired && failure.length === 0">
          <CircleAlertIcon />
          <AlertDescription>{{ $t('auth.sessionExpired') }}</AlertDescription>
        </Alert>
        <Alert v-if="failure.length > 0" variant="destructive" data-testid="login-error">
          <CircleAlertIcon />
          <AlertTitle>{{ $t('auth.loginFailed') }}</AlertTitle>
          <AlertDescription>
            <p v-for="message in failure" :key="message">{{ message }}</p>
          </AlertDescription>
        </Alert>

        <FormField id="email" v-slot="aria" :label="$t('auth.email')" :error="errors.email">
          <Input id="email" v-model="email" v-bind="{ ...emailAttrs, ...aria }" type="email" autocomplete="username" />
        </FormField>
        <FormField id="password" v-slot="aria" :label="$t('auth.password')" :error="errors.password">
          <Input
            id="password"
            v-model="password"
            v-bind="{ ...passwordAttrs, ...aria }"
            type="password"
            autocomplete="current-password"
          />
        </FormField>
        <div class="flex items-center gap-2">
          <Checkbox id="remember-me" v-model="rememberMe" />
          <Label for="remember-me" class="font-normal">{{ $t('auth.rememberMe') }}</Label>
        </div>
      </CardContent>
      <CardFooter class="mt-6 flex flex-col gap-4">
        <Button type="submit" class="w-full" :disabled="isSubmitting">
          <Loader2Icon v-if="isSubmitting" class="animate-spin" />
          {{ $t('auth.login') }}
        </Button>
        <p v-if="auth.registrationOpen" class="text-muted-foreground text-sm">
          {{ $t('auth.noAccount') }}
          <RouterLink :to="{ name: 'register' }" class="text-foreground underline underline-offset-4">
            {{ $t('auth.register') }}
          </RouterLink>
        </p>
      </CardFooter>
    </form>
  </Card>
</template>
