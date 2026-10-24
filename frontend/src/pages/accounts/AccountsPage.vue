<script setup lang="ts">
import AccountStatusBadge from '@/components/accounts/AccountStatusBadge.vue'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { errorMessages } from '@/lib/errors'
import { formatRelative } from '@/lib/format'
import { useAccountsStore } from '@/stores/accounts'
import { MailPlusIcon, PlusIcon } from '@lucide/vue'
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterLink } from 'vue-router'
import { toast } from 'vue-sonner'

const store = useAccountsStore()
const { t, locale } = useI18n()
const loading = ref(!store.loaded)

onMounted(async () => {
  try {
    await store.fetchAll()
  } catch (error) {
    toast.error(errorMessages(error, t('common.unexpectedError')).join(' '))
  } finally {
    loading.value = false
  }
})
</script>

<template>
  <div class="mx-auto grid max-w-5xl gap-6">
    <div class="flex flex-wrap items-end justify-between gap-4">
      <div>
        <h2 class="text-2xl font-semibold tracking-tight">{{ $t('accounts.title') }}</h2>
        <p class="text-muted-foreground mt-1">{{ $t('accounts.subtitle') }}</p>
      </div>
      <Button v-if="store.accounts.length > 0" as-child>
        <RouterLink :to="{ name: 'account-new' }">
          <PlusIcon />
          {{ $t('accounts.add') }}
        </RouterLink>
      </Button>
    </div>

    <div v-if="loading && store.accounts.length === 0" class="grid gap-4 md:grid-cols-2">
      <Skeleton v-for="i in 2" :key="i" class="h-36 rounded-xl" />
    </div>

    <Card v-else-if="store.accounts.length === 0" class="items-center py-12 text-center">
      <CardContent class="grid justify-items-center gap-4">
        <span class="bg-muted flex size-12 items-center justify-center rounded-full">
          <MailPlusIcon class="text-muted-foreground size-6" />
        </span>
        <div>
          <p class="font-medium">{{ $t('accounts.empty') }}</p>
          <p class="text-muted-foreground mt-1 text-sm">{{ $t('accounts.emptyHint') }}</p>
        </div>
        <Button as-child>
          <RouterLink :to="{ name: 'account-new' }">
            <PlusIcon />
            {{ $t('accounts.add') }}
          </RouterLink>
        </Button>
      </CardContent>
    </Card>

    <ul v-else class="grid gap-4 md:grid-cols-2" role="list">
      <li v-for="account in store.accounts" :key="account.id">
        <RouterLink
          :to="{ name: 'account-detail', params: { id: account.id } }"
          class="focus-visible:ring-ring/50 block rounded-xl outline-none focus-visible:ring-3"
          :data-testid="`account-card-${account.name}`"
        >
          <Card class="hover:bg-accent/40 h-full transition-colors">
            <CardHeader>
              <div class="flex items-start justify-between gap-2">
                <CardTitle class="truncate">{{ account.name }}</CardTitle>
                <AccountStatusBadge :status="account.status" :enabled="account.enabled" />
              </div>
              <CardDescription class="truncate">{{ account.username }} · {{ account.host }}</CardDescription>
            </CardHeader>
            <CardContent class="text-muted-foreground grid gap-1 text-sm">
              <p v-if="account.status === 'Error' && account.lastErrorCode" class="text-destructive">
                {{ $t(`connectionErrors.${account.lastErrorCode}`) }}
              </p>
              <p>
                {{
                  account.lastCheckedAt
                    ? $t('accounts.lastChecked', { time: formatRelative(account.lastCheckedAt, locale) })
                    : $t('accounts.neverChecked')
                }}
              </p>
              <p>{{ $t('accounts.folders.watched') }}: {{ account.watchedFolders.join(', ') }}</p>
            </CardContent>
          </Card>
        </RouterLink>
      </li>
    </ul>
  </div>
</template>
