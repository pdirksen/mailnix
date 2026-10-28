<script setup lang="ts">
import { ApiError, type Schemas } from '@/api/client'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { useConnectionErrors } from '@/lib/connectionErrors'
import { useAssistantsStore } from '@/stores/assistants'
import { useAuthStore } from '@/stores/auth'
import { CheckIcon, Loader2Icon, PlugIcon, XIcon } from '@lucide/vue'
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'

/**
 * The consent step when an AI app (e.g. a Claude.ai connector) connects through OAuth: /oauth2/authorize sends the
 * browser here with the request's parameters. The decision goes to the API, which answers with the address to send
 * the browser back to (with the code, or with an error).
 */
const { t } = useI18n()
const route = useRoute()
const auth = useAuthStore()
const store = useAssistantsStore()
const { describeRequestError } = useConnectionErrors()

function param(name: string) {
  const value = route.query[name]
  return typeof value === 'string' && value.length > 0 ? value : undefined
}

const app = ref<Schemas['ConnectAppResponse']>()
const failure = ref<string>()
const allowChanges = ref(false)
const busy = ref<'allow' | 'deny'>()

onMounted(async () => {
  const clientId = param('client_id')
  const redirectUri = param('redirect_uri')
  if (!clientId || !redirectUri) {
    failure.value = t('connect.invalid')
    return
  }
  try {
    app.value = await store.connectingApp(clientId, redirectUri, param('scope'))
    allowChanges.value = app.value.requestedScope === 'ReadWrite'
  } catch (error) {
    failure.value = error instanceof ApiError && error.code === 'ConnectInvalidApp' ? t('connect.invalid') : describeRequestError(error).message
  }
})

async function decide(approve: boolean) {
  busy.value = approve ? 'allow' : 'deny'
  try {
    const redirectUrl = await store.decide({
      clientId: param('client_id')!,
      redirectUri: param('redirect_uri')!,
      responseType: param('response_type') ?? null,
      codeChallenge: param('code_challenge') ?? null,
      codeChallengeMethod: param('code_challenge_method') ?? null,
      state: param('state') ?? null,
      resource: param('resource') ?? null,
      approve,
      allowChanges: approve && allowChanges.value,
    })
    window.location.assign(redirectUrl)
  } catch (error) {
    failure.value = describeRequestError(error).message
    busy.value = undefined
  }
}
</script>

<template>
  <Card>
    <CardHeader>
      <CardTitle class="flex items-center gap-2"><PlugIcon class="size-4" />{{ $t('connect.title') }}</CardTitle>
      <CardDescription v-if="app">
        <i18n-t keypath="connect.question" tag="span">
          <template #app><strong class="text-foreground" data-testid="connect-app">{{ app.name }}</strong></template>
        </i18n-t>
      </CardDescription>
    </CardHeader>

    <CardContent v-if="failure">
      <p class="text-sm" role="alert" data-testid="connect-error">{{ failure }}</p>
    </CardContent>

    <CardContent v-else-if="!app" class="grid gap-3">
      <Skeleton class="h-5 w-3/4" />
      <Skeleton class="h-20" />
    </CardContent>

    <template v-else>
      <CardContent class="grid gap-4 text-sm">
        <p class="text-muted-foreground">
          {{ $t('connect.account', { email: auth.user?.email ?? '' }) }}
          {{ $t('connect.returnsTo', { host: app.redirectHost }) }}
        </p>
        <ul class="grid gap-1.5" role="list">
          <li class="flex gap-2"><CheckIcon class="mt-0.5 size-4 shrink-0 text-emerald-600" />{{ $t('connect.canRead') }}</li>
          <li class="flex gap-2">
            <component :is="allowChanges ? CheckIcon : XIcon" :class="['mt-0.5 size-4 shrink-0', allowChanges ? 'text-emerald-600' : 'text-muted-foreground']" />
            {{ $t('connect.canChange') }}
          </li>
          <li class="text-muted-foreground flex gap-2"><XIcon class="mt-0.5 size-4 shrink-0" />{{ $t('connect.never') }}</li>
        </ul>
        <div class="flex items-start gap-3 rounded-lg border p-3">
          <Checkbox id="allow-changes" v-model="allowChanges" class="mt-0.5" data-testid="allow-changes" />
          <Label for="allow-changes" class="grid gap-1 font-normal">
            <span class="font-medium">{{ $t('connect.allowChanges') }}</span>
            <span class="text-muted-foreground text-xs">{{ $t('connect.allowChangesHint') }}</span>
          </Label>
        </div>
      </CardContent>
      <CardFooter class="justify-end gap-2">
        <Button variant="outline" :disabled="!!busy" data-testid="connect-deny" @click="decide(false)">
          <Loader2Icon v-if="busy === 'deny'" class="animate-spin" />
          {{ $t('connect.deny') }}
        </Button>
        <Button :disabled="!!busy" data-testid="connect-allow" @click="decide(true)">
          <Loader2Icon v-if="busy === 'allow'" class="animate-spin" />
          {{ $t('connect.allow') }}
        </Button>
      </CardFooter>
    </template>
  </Card>
</template>
