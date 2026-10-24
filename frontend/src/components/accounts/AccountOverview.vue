<script setup lang="ts">
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { formatDateTime, formatRelative } from '@/lib/format'
import type { Account, SpecialFolders } from '@/stores/accounts'
import { useI18n } from 'vue-i18n'
import AccountStatusBadge from './AccountStatusBadge.vue'
import CapabilityList from './CapabilityList.vue'
import OAuthConnectButton from './OAuthConnectButton.vue'
import { SPECIAL_FOLDER_ICONS } from './folderIcons'

defineProps<{ account: Account }>()
const { locale } = useI18n()

const SPECIAL = [
  { key: 'trash', kind: 'Trash' },
  { key: 'junk', kind: 'Junk' },
  { key: 'archive', kind: 'Archive' },
  { key: 'sent', kind: 'Sent' },
  { key: 'drafts', kind: 'Drafts' },
] as const satisfies readonly { key: keyof SpecialFolders; kind: string }[]
</script>

<template>
  <div class="grid gap-4 md:grid-cols-2">
    <Card>
      <CardHeader>
        <CardTitle class="text-base">{{ $t('accounts.overview.connection') }}</CardTitle>
      </CardHeader>
      <CardContent class="grid gap-2 text-sm">
        <AccountStatusBadge :status="account.status" :enabled="account.enabled" />
        <p v-if="account.lastCheckedAt" class="text-muted-foreground" :title="formatDateTime(account.lastCheckedAt, locale)">
          {{ $t('accounts.lastChecked', { time: formatRelative(account.lastCheckedAt, locale) }) }}
        </p>
        <p v-else class="text-muted-foreground">{{ $t('accounts.neverChecked') }}</p>
        <template v-if="account.status === 'Error'">
          <p class="text-destructive">{{ $t(`connectionErrors.${account.lastErrorCode ?? 'Unknown'}`) }}</p>
          <p v-if="account.lastError" class="text-muted-foreground font-mono text-xs break-all">{{ account.lastError }}</p>
          <OAuthConnectButton
            v-if="account.provider !== 'Imap' && (account.lastErrorCode === 'ReconnectRequired' || account.lastErrorCode === 'AuthenticationFailed')"
            :provider="account.provider"
            :account-id="account.id"
            class="w-fit"
          />
        </template>
      </CardContent>
    </Card>

    <Card>
      <CardHeader>
        <CardTitle class="text-base">{{ $t('accounts.overview.server') }}</CardTitle>
      </CardHeader>
      <CardContent>
        <dl class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm">
          <dt class="text-muted-foreground">{{ $t('accounts.fields.provider') }}</dt>
          <dd>{{ $t(`accounts.provider.${account.provider}`) }}</dd>
          <dt class="text-muted-foreground">{{ $t('accounts.fields.host') }}</dt>
          <dd class="truncate">{{ account.host }}:{{ account.port }}</dd>
          <dt class="text-muted-foreground">{{ $t('accounts.fields.security') }}</dt>
          <dd>{{ $t(`accounts.security.${account.security}`) }}</dd>
          <dt class="text-muted-foreground">{{ $t('accounts.fields.username') }}</dt>
          <dd class="truncate">{{ account.username }}</dd>
        </dl>
      </CardContent>
    </Card>

    <Card>
      <CardHeader>
        <CardTitle class="text-base">{{ $t('accounts.folders.title') }}</CardTitle>
      </CardHeader>
      <CardContent class="grid gap-3 text-sm">
        <div>
          <p class="text-muted-foreground">{{ $t('accounts.folders.watched') }}</p>
          <p data-testid="watched-folders">{{ account.watchedFolders.join(', ') }}</p>
        </div>
        <ul class="grid gap-1" role="list">
          <li v-for="{ key, kind } in SPECIAL" :key="key" class="flex items-center gap-2">
            <component :is="SPECIAL_FOLDER_ICONS[kind]" class="text-muted-foreground size-4" aria-hidden="true" />
            <span class="text-muted-foreground">{{ $t(`accounts.folders.special.${kind}`) }}:</span>
            <span class="truncate">{{ account.specialFolders[key] ?? $t('accounts.folders.notSet') }}</span>
          </li>
        </ul>
      </CardContent>
    </Card>

    <Card>
      <CardHeader>
        <CardTitle class="text-base">{{ $t('accounts.overview.capabilities') }}</CardTitle>
      </CardHeader>
      <CardContent>
        <CapabilityList :capabilities="account.capabilities" />
      </CardContent>
    </Card>
  </div>
</template>
