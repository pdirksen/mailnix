<script setup lang="ts">
import { api, ApiError, type Schemas } from '@/api/client'
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
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterLink, useRoute, useRouter } from 'vue-router'
import { z } from 'zod'

/** Must match the server's password policy (Identity RequiredLength). */
const MIN_PASSWORD_LENGTH = 10

const { t, te } = useI18n()
const auth = useAuthStore()
const route = useRoute()
const router = useRouter()

/** From an invitation link (/register?invite=…): joins the inviting organization instead of founding one. */
const inviteToken = typeof route.query.invite === 'string' ? route.query.invite : undefined
const invitation = ref<Schemas['InvitationInfo']>()
const invitationInvalid = ref(false)
const askOrganization = computed(() => auth.multiTenant && !inviteToken)
/** The first account needs the setup code from the server log; links in the log carry it (?setup=…). */
const askSetupCode = computed(() => auth.setupCodeRequired && !inviteToken)

const schema = z
  .object({
    setupCode: z.string().refine((value) => !askSetupCode.value || value.trim().length > 0, { error: () => t('validation.required') }),
    email: z.email({ error: () => t('validation.email') }).max(256, { error: () => t('validation.maxLength', { max: 256 }) }),
    displayName: z.string().max(200, { error: () => t('validation.maxLength', { max: 200 }) }),
    organizationName: z.string().max(200, { error: () => t('validation.maxLength', { max: 200 }) }),
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
  initialValues: {
    setupCode: typeof route.query.setup === 'string' ? route.query.setup : '',
    email: '',
    displayName: '',
    organizationName: '',
    password: '',
    confirmPassword: '',
  },
})
const [setupCode, setupCodeAttrs] = defineField('setupCode')
const [email, emailAttrs] = defineField('email')
const [displayName, displayNameAttrs] = defineField('displayName')
const [organizationName, organizationNameAttrs] = defineField('organizationName')
const [password, passwordAttrs] = defineField('password')
const [confirmPassword, confirmPasswordAttrs] = defineField('confirmPassword')

onMounted(async () => {
  if (!inviteToken) return
  const result = await api.GET('/api/auth/invitations/{token}', { params: { path: { token: inviteToken } } }).catch(() => undefined)
  if (result?.data) {
    invitation.value = result.data
    if (result.data.email) email.value = result.data.email
  } else {
    invitationInvalid.value = true
  }
})

const failure = ref<string[]>([])

const onSubmit = handleSubmit(async ({ setupCode, email, displayName, organizationName, password }) => {
  failure.value = []
  try {
    await auth.register({
      email,
      password,
      displayName: displayName.trim() || null,
      organizationName: askOrganization.value ? organizationName.trim() || null : null,
      inviteToken: inviteToken ?? null,
      setupCode: askSetupCode.value ? setupCode.trim() : null,
    })
    await router.replace({ name: 'dashboard' })
  } catch (error) {
    const key = `auth.errors.${error instanceof ApiError ? error.code : ''}`
    failure.value = te(key) ? [t(key)] : errorMessages(error, t('common.unexpectedError'))
  }
})
</script>

<template>
  <Card>
    <CardHeader>
      <CardTitle class="text-xl">
        {{ invitation ? $t('auth.joinTitle', { organization: invitation.tenantName }) : $t('auth.registerTitle') }}
      </CardTitle>
      <CardDescription>
        {{
          invitation
            ? $t('auth.joinSubtitle', { organization: invitation.tenantName })
            : auth.multiTenant
              ? $t('auth.registerSubtitleOrganization')
              : $t('auth.registerSubtitle')
        }}
      </CardDescription>
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

        <Alert v-if="invitationInvalid" variant="destructive" data-testid="invitation-invalid">
          <CircleAlertIcon />
          <AlertTitle>{{ $t('auth.errors.InvitationInvalid') }}</AlertTitle>
          <AlertDescription>{{ $t('auth.invitationInvalidHint') }}</AlertDescription>
        </Alert>

        <FormField
          v-if="askSetupCode"
          id="setup-code"
          v-slot="aria"
          :label="$t('auth.setupCode')"
          :hint="$t('auth.setupCodeHint')"
          :error="errors.setupCode"
        >
          <Input
            id="setup-code"
            v-model="setupCode"
            v-bind="{ ...setupCodeAttrs, ...aria }"
            autocomplete="off"
            spellcheck="false"
            class="font-mono uppercase"
          />
        </FormField>
        <FormField id="email" v-slot="aria" :label="$t('auth.email')" :error="errors.email">
          <Input
            id="email"
            v-model="email"
            v-bind="{ ...emailAttrs, ...aria }"
            type="email"
            autocomplete="email"
            :readonly="!!invitation?.email"
          />
        </FormField>
        <FormField
          v-if="askOrganization"
          id="organization"
          v-slot="aria"
          :label="$t('auth.organization')"
          :hint="$t('auth.organizationHint')"
          :error="errors.organizationName"
        >
          <Input
            id="organization"
            v-model="organizationName"
            v-bind="{ ...organizationNameAttrs, ...aria }"
            autocomplete="organization"
          />
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
