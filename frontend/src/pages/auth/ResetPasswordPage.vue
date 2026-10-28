<script setup lang="ts">
import { api, ApiError, unwrap } from '@/api/client'
import FormField from '@/components/common/FormField.vue'
import PasswordStrength from '@/components/common/PasswordStrength.vue'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { errorMessages } from '@/lib/errors'
import { CircleAlertIcon, Loader2Icon } from '@lucide/vue'
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterLink, useRoute, useRouter } from 'vue-router'

/** Must match the server's password policy (Identity RequiredLength). */
const MIN_PASSWORD_LENGTH = 10

const { t } = useI18n()
const route = useRoute()
const router = useRouter()

// From the link in the email.
const email = typeof route.query.email === 'string' ? route.query.email : ''
const token = typeof route.query.token === 'string' ? route.query.token : ''

const password = ref('')
const repeat = ref('')
const errors = ref<{ password?: string; repeat?: string }>({})
const failure = ref<string[]>([])
const saving = ref(false)
/** The link is broken, used or expired: a new one has to be requested. */
const linkInvalid = ref(!email || !token)
const retryLink = computed(() => ({ name: 'forgot-password', query: email ? { email } : {} }))

async function save() {
  errors.value = {
    password: password.value.length < MIN_PASSWORD_LENGTH ? t('validation.passwordLength', { min: MIN_PASSWORD_LENGTH }) : undefined,
    repeat: repeat.value === password.value ? undefined : t('validation.passwordMismatch'),
  }
  if (errors.value.password || errors.value.repeat) return
  failure.value = []
  saving.value = true
  try {
    unwrap(await api.POST('/api/auth/reset-password', { body: { email, token, newPassword: password.value } }))
    await router.replace({ name: 'login', query: { reset: '1' } })
  } catch (error) {
    if (error instanceof ApiError && error.code === 'ResetLinkInvalid') linkInvalid.value = true
    else failure.value = errorMessages(error, t('common.unexpectedError'))
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <Card>
    <CardHeader>
      <CardTitle class="text-xl">{{ $t('auth.reset.title') }}</CardTitle>
      <CardDescription>{{ email ? $t('auth.reset.subtitle', { email }) : $t('auth.reset.subtitleNoEmail') }}</CardDescription>
    </CardHeader>

    <template v-if="linkInvalid">
      <CardContent>
        <Alert variant="destructive" data-testid="reset-link-invalid">
          <CircleAlertIcon />
          <AlertTitle>{{ $t('auth.errors.ResetLinkInvalid') }}</AlertTitle>
          <AlertDescription>{{ $t('auth.reset.invalidHint') }}</AlertDescription>
        </Alert>
      </CardContent>
      <CardFooter class="mt-6 flex flex-col gap-3">
        <Button as-child class="w-full">
          <RouterLink :to="retryLink">{{ $t('auth.reset.requestNew') }}</RouterLink>
        </Button>
      </CardFooter>
    </template>

    <form v-else novalidate @submit.prevent="save">
      <CardContent class="grid gap-4">
        <Alert v-if="failure.length > 0" variant="destructive">
          <CircleAlertIcon />
          <AlertDescription>
            <p v-for="message in failure" :key="message">{{ message }}</p>
          </AlertDescription>
        </Alert>
        <!-- Lets password managers save the new password for the right account. -->
        <input type="email" :value="email" autocomplete="username" class="hidden" readonly aria-hidden="true" tabindex="-1" />
        <FormField id="new-password" v-slot="aria" :label="$t('settings.security.newPassword')" :error="errors.password">
          <Input id="new-password" v-model="password" v-bind="aria" type="password" autocomplete="new-password" />
        </FormField>
        <PasswordStrength :password="password" />
        <FormField id="repeat-password" v-slot="aria" :label="$t('auth.confirmPassword')" :error="errors.repeat">
          <Input id="repeat-password" v-model="repeat" v-bind="aria" type="password" autocomplete="new-password" />
        </FormField>
        <p class="text-muted-foreground text-xs">{{ $t('auth.reset.signedOutHint') }}</p>
      </CardContent>
      <CardFooter class="mt-6 flex flex-col gap-4">
        <Button type="submit" class="w-full" :disabled="saving" data-testid="save-new-password">
          <Loader2Icon v-if="saving" class="animate-spin" />
          {{ $t('auth.reset.save') }}
        </Button>
      </CardFooter>
    </form>
  </Card>
</template>
