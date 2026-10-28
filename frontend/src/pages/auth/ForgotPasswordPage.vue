<script setup lang="ts">
import { api, unwrap } from '@/api/client'
import FormField from '@/components/common/FormField.vue'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { errorMessages } from '@/lib/errors'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import { CircleAlertIcon, Loader2Icon, MailCheckIcon } from '@lucide/vue'
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterLink, useRoute } from 'vue-router'
import { z } from 'zod'

const { t } = useI18n()
const auth = useAuthStore()
const ui = useUiStore()
const route = useRoute()

const email = ref(typeof route.query.email === 'string' ? route.query.email : '')
const emailError = ref<string>()
const failure = ref<string[]>([])
const sending = ref(false)
/** The address the link went to; the answer is the same whether or not it has an account. */
const sentTo = ref<string>()

async function send() {
  const address = email.value.trim()
  emailError.value = z.email().safeParse(address).success ? undefined : t('validation.email')
  if (emailError.value) return
  failure.value = []
  sending.value = true
  try {
    unwrap(await api.POST('/api/auth/forgot-password', { body: { email: address, language: ui.locale } }))
    sentTo.value = address
  } catch (error) {
    failure.value = errorMessages(error, t('common.unexpectedError'))
  } finally {
    sending.value = false
  }
}
</script>

<template>
  <Card>
    <template v-if="sentTo">
      <CardHeader>
        <span class="bg-primary/10 text-primary mb-2 flex size-10 items-center justify-center rounded-full">
          <MailCheckIcon class="size-5" />
        </span>
        <CardTitle class="text-xl">{{ $t('auth.forgot.sentTitle') }}</CardTitle>
        <CardDescription data-testid="reset-link-sent">{{ $t('auth.forgot.sent', { email: sentTo }) }}</CardDescription>
      </CardHeader>
      <CardContent>
        <p class="text-muted-foreground text-sm">{{ $t('auth.forgot.notArrived') }}</p>
      </CardContent>
      <CardFooter class="mt-6 flex flex-col gap-3">
        <Button as-child class="w-full">
          <RouterLink :to="{ name: 'login' }">{{ $t('auth.forgot.backToLogin') }}</RouterLink>
        </Button>
        <Button variant="ghost" class="w-full" :disabled="sending" @click="send">
          <Loader2Icon v-if="sending" class="animate-spin" />
          {{ $t('auth.forgot.sendAgain') }}
        </Button>
      </CardFooter>
    </template>

    <template v-else>
      <CardHeader>
        <CardTitle class="text-xl">{{ $t('auth.forgot.title') }}</CardTitle>
        <CardDescription>{{ $t('auth.forgot.subtitle') }}</CardDescription>
      </CardHeader>
      <form novalidate @submit.prevent="send">
        <CardContent class="grid gap-4">
          <Alert v-if="!auth.passwordResetAvailable" data-testid="reset-unavailable">
            <CircleAlertIcon />
            <AlertDescription>{{ $t('auth.forgot.unavailable') }}</AlertDescription>
          </Alert>
          <Alert v-if="failure.length > 0" variant="destructive">
            <CircleAlertIcon />
            <AlertDescription>
              <p v-for="message in failure" :key="message">{{ message }}</p>
            </AlertDescription>
          </Alert>
          <FormField id="email" v-slot="aria" :label="$t('auth.email')" :error="emailError">
            <Input id="email" v-model="email" v-bind="aria" type="email" autocomplete="username" :disabled="!auth.passwordResetAvailable" />
          </FormField>
        </CardContent>
        <CardFooter class="mt-6 flex flex-col gap-4">
          <Button type="submit" class="w-full" :disabled="sending || !auth.passwordResetAvailable" data-testid="send-reset-link">
            <Loader2Icon v-if="sending" class="animate-spin" />
            {{ $t('auth.forgot.send') }}
          </Button>
          <RouterLink :to="{ name: 'login' }" class="text-muted-foreground text-sm underline-offset-4 hover:underline">
            {{ $t('auth.forgot.backToLogin') }}
          </RouterLink>
        </CardFooter>
      </form>
    </template>
  </Card>
</template>
