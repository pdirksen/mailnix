<script setup lang="ts">
import FormField from '@/components/common/FormField.vue'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { errorMessages } from '@/lib/errors'
import { toTypedSchema } from '@/lib/zodForm'
import { safeRedirect } from '@/router/safeRedirect'
import { useAuthStore } from '@/stores/auth'
import { CircleAlertIcon, Loader2Icon } from '@lucide/vue'
import { useForm } from 'vee-validate'
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterLink, useRoute, useRouter } from 'vue-router'
import { z } from 'zod'

const { t } = useI18n()
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

const onSubmit = handleSubmit(async (values) => {
  failure.value = []
  try {
    await auth.login(values)
    await router.replace(safeRedirect(route.query.redirect))
  } catch (error) {
    failure.value = errorMessages(error, t('common.unexpectedError'))
  }
})
</script>

<template>
  <Card>
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
