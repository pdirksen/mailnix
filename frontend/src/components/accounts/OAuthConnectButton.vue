<script setup lang="ts">
import { Button } from '@/components/ui/button'
import { useConnectionErrors } from '@/lib/connectionErrors'
import { type OAuthProvider, useAccountsStore } from '@/stores/accounts'
import { Loader2Icon, LogInIcon } from '@lucide/vue'
import { ref } from 'vue'
import { toast } from 'vue-sonner'

const props = defineProps<{
  provider: OAuthProvider
  /** Reconnect this account instead of adding a new one. */
  accountId?: string
  /** Name for a new account. */
  name?: string
  variant?: 'default' | 'outline'
}>()

const store = useAccountsStore()
const { describeRequestError } = useConnectionErrors()
const redirecting = ref(false)

async function start() {
  redirecting.value = true
  try {
    await store.startOAuth(props.provider, { name: props.name, accountId: props.accountId })
    // The browser is leaving for the provider; keep the spinner until it does.
  } catch (error) {
    toast.error(describeRequestError(error).message)
    redirecting.value = false
  }
}
</script>

<template>
  <Button :variant="variant ?? 'default'" :disabled="redirecting" :data-testid="`oauth-${provider}`" @click="start">
    <Loader2Icon v-if="redirecting" class="animate-spin" />
    <LogInIcon v-else />
    {{ accountId ? $t('oauth.reconnect', { provider: $t(`accounts.provider.${provider}`) }) : $t('oauth.signIn', { provider: $t(`accounts.provider.${provider}`) }) }}
  </Button>
</template>
