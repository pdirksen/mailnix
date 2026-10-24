<script setup lang="ts">
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { Switch } from '@/components/ui/switch'
import { type DescribedError, useConnectionErrors } from '@/lib/connectionErrors'
import { useSchemaValidation } from '@/lib/zodForm'
import { type Account, type FolderInfo, type SpecialFolders, useAccountsStore } from '@/stores/accounts'
import { Loader2Icon } from '@lucide/vue'
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { toast } from 'vue-sonner'
import { Input } from '@/components/ui/input'
import type { OAuthProvider } from '@/stores/accounts'
import ConnectionErrorAlert from './ConnectionErrorAlert.vue'
import OAuthConnectButton from './OAuthConnectButton.vue'
import FolderTree from './FolderTree.vue'
import SafetyFields from './SafetyFields.vue'
import { safetySchema, type SafetySettings } from './safety'
import ServerSettingsFields from './ServerSettingsFields.vue'
import { serverSettingsSchema, type ServerSettings } from './serverSettings'
import SpecialFoldersFields from './SpecialFoldersFields.vue'

const props = defineProps<{ account: Account }>()

const { t } = useI18n()
const store = useAccountsStore()
const { describeRequestError } = useConnectionErrors()

const server = ref<ServerSettings>({
  name: props.account.name,
  host: props.account.host,
  port: props.account.port,
  security: props.account.security,
  username: props.account.username,
  password: '',
})
const enabled = ref(props.account.enabled)
const watched = ref<string[]>([...props.account.watchedFolders])
const specialFolders = ref<SpecialFolders>({ ...props.account.specialFolders })
const safety = ref<SafetySettings>({
  blastRadiusLimit: props.account.blastRadiusLimit,
  purgeTrashAfterDays: props.account.purgeTrashAfterDays,
})

// Google / Microsoft accounts: server and user come from the provider; only the name is edited here.
const oauthProvider = computed(() => (props.account.provider === 'Imap' ? null : (props.account.provider as OAuthProvider)))
const nameError = ref<string>()

const serverValidation = useSchemaValidation(computed(() => serverSettingsSchema(t, { passwordRequired: false })))
const safetyValidation = useSchemaValidation(computed(() => safetySchema(t)))
const watchedError = ref<string>()

// The folder pickers need the server's folder list; without it the current values are still shown and kept.
const folders = ref<FolderInfo[] | null>(null)
const foldersError = ref<DescribedError | null>(null)

onMounted(async () => {
  try {
    folders.value = (await store.folders(props.account.id, false)).folders
  } catch (error) {
    foldersError.value = describeRequestError(error)
  }
})

const saving = ref(false)
const saveError = ref<DescribedError | null>(null)

async function save() {
  nameError.value = oauthProvider.value && !server.value.name.trim() ? t('validation.required') : undefined
  const validServer = oauthProvider.value ? (nameError.value ? false : { ...server.value, name: server.value.name.trim() }) : serverValidation.validate(server.value)
  const validSafety = safetyValidation.validate(safety.value)
  watchedError.value = watched.value.length === 0 ? t('validation.watchedRequired') : undefined
  if (!validServer || !validSafety || watchedError.value) return

  saving.value = true
  saveError.value = null
  try {
    await store.update(props.account.id, {
      name: validServer.name,
      host: validServer.host,
      port: validServer.port,
      security: validServer.security,
      username: validServer.username,
      password: validServer.password || null,
      enabled: enabled.value,
      watchedFolders: watched.value,
      specialFolders: specialFolders.value,
      blastRadiusLimit: validSafety.blastRadiusLimit,
      purgeTrashAfterDays: validSafety.purgeTrashAfterDays,
    })
    server.value.password = ''
    toast.success(t('common.saved'))
  } catch (error) {
    saveError.value = describeRequestError(error)
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <form class="grid gap-4" novalidate @submit.prevent="save">
    <Card>
      <CardContent class="flex items-start gap-3">
        <Switch id="account-enabled" v-model="enabled" class="mt-0.5" />
        <Label for="account-enabled" class="grid gap-1 font-normal">
          <span class="font-medium">{{ $t('accounts.fields.enabled') }}</span>
          <span class="text-muted-foreground text-xs">{{ $t('accounts.fields.enabledHint') }}</span>
        </Label>
      </CardContent>
    </Card>

    <Card>
      <CardHeader>
        <CardTitle>{{ $t('accounts.overview.server') }}</CardTitle>
      </CardHeader>
      <CardContent v-if="oauthProvider" class="grid gap-4">
        <div class="grid gap-2">
          <Label for="account-name">{{ $t('accounts.fields.name') }}</Label>
          <Input id="account-name" v-model="server.name" class="max-w-sm" :aria-invalid="!!nameError" autocomplete="off" />
          <p v-if="nameError" class="text-destructive text-sm" role="alert">{{ nameError }}</p>
        </div>
        <div class="flex flex-wrap items-center justify-between gap-3 rounded-lg border p-3 text-sm">
          <span>{{ $t('oauth.signedInAs', { provider: $t(`accounts.provider.${oauthProvider}`), email: account.username }) }}</span>
          <OAuthConnectButton :provider="oauthProvider" :account-id="account.id" variant="outline" />
        </div>
      </CardContent>
      <CardContent v-else>
        <ServerSettingsFields v-model="server" :errors="serverValidation.errors.value" password-optional />
      </CardContent>
    </Card>

    <Card>
      <CardHeader>
        <CardTitle>{{ $t('accounts.folders.watched') }}</CardTitle>
        <CardDescription>{{ $t('accounts.folders.watchedHint') }}</CardDescription>
      </CardHeader>
      <CardContent class="grid gap-2">
        <ConnectionErrorAlert v-if="foldersError" :title="$t('accounts.folders.loadFailed')" :error="foldersError" />
        <div v-else-if="!folders" class="grid gap-2"><Skeleton v-for="i in 4" :key="i" class="h-10" /></div>
        <div v-else class="max-h-96 overflow-y-auto">
          <FolderTree v-model="watched" :folders="folders" selectable />
        </div>
        <p v-if="watchedError" class="text-destructive text-sm" role="alert">{{ watchedError }}</p>
      </CardContent>
    </Card>

    <Card v-if="folders">
      <CardHeader>
        <CardTitle>{{ $t('accounts.folders.specialTitle') }}</CardTitle>
        <CardDescription>{{ $t('accounts.folders.specialHint') }}</CardDescription>
      </CardHeader>
      <CardContent>
        <SpecialFoldersFields v-model="specialFolders" :folders="folders" />
      </CardContent>
    </Card>

    <Card>
      <CardHeader>
        <CardTitle>{{ $t('accounts.safety.title') }}</CardTitle>
      </CardHeader>
      <CardContent>
        <SafetyFields v-model="safety" :errors="safetyValidation.errors.value" :last-purge-at="account.lastPurgeAt" />
      </CardContent>
    </Card>

    <ConnectionErrorAlert v-if="saveError" :title="$t('accounts.saveFailed')" :error="saveError" />

    <div class="flex justify-end">
      <Button type="submit" :disabled="saving" data-testid="save-account">
        <Loader2Icon v-if="saving" class="animate-spin" />
        {{ $t('common.save') }}
      </Button>
    </div>
  </form>
</template>
