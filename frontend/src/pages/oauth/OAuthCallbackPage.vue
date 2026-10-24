<script setup lang="ts">
import ConnectionErrorAlert from '@/components/accounts/ConnectionErrorAlert.vue'
import { Button } from '@/components/ui/button'
import { type DescribedError, useConnectionErrors } from '@/lib/connectionErrors'
import { useAccountsStore } from '@/stores/accounts'
import { Loader2Icon } from '@lucide/vue'
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterLink, useRoute, useRouter } from 'vue-router'
import { toast } from 'vue-sonner'

/**
 * Google / Microsoft send the browser back here after the sign-in. The page hands code and state to the API
 * (a same-site request, so the login cookie is sent) and opens the account.
 */
const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const store = useAccountsStore()
const { describeRequestError } = useConnectionErrors()

const failure = ref<DescribedError | null>(null)

function text(value: unknown) {
  return typeof value === 'string' && value.length > 0 ? value : undefined
}

onMounted(async () => {
  const { code, state, error, error_description: errorDescription } = route.query
  // The code is single-use and must not stay in the address bar or the history.
  await router.replace({ name: 'oauth-callback' })

  if (text(error)) {
    failure.value =
      error === 'access_denied'
        ? { message: t('oauth.cancelled') }
        : { message: t('oauth.providerError'), detail: text(errorDescription) ?? text(error) }
    return
  }
  if (!text(code) || !text(state)) {
    failure.value = { message: t('oauth.invalidCallback') }
    return
  }

  try {
    const result = await store.completeOAuth(text(state)!, text(code)!)
    toast.success(result.created ? t('oauth.added') : t('oauth.reconnected'))
    await router.replace({ name: 'account-detail', params: { id: result.account.id }, query: result.created ? { tab: 'settings' } : {} })
  } catch (e) {
    failure.value = describeRequestError(e)
  }
})
</script>

<template>
  <div class="mx-auto grid max-w-xl gap-4 py-8">
    <template v-if="failure">
      <ConnectionErrorAlert :title="$t('oauth.failed')" :error="failure" />
      <div class="flex gap-2">
        <Button as-child><RouterLink :to="{ name: 'account-new' }">{{ $t('oauth.tryAgain') }}</RouterLink></Button>
        <Button variant="outline" as-child><RouterLink :to="{ name: 'accounts' }">{{ $t('accounts.title') }}</RouterLink></Button>
      </div>
    </template>
    <p v-else class="text-muted-foreground flex items-center gap-2" data-testid="oauth-completing">
      <Loader2Icon class="size-4 animate-spin" />
      {{ $t('oauth.completing') }}
    </p>
  </div>
</template>
