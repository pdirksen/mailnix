<script setup lang="ts">
import CapabilityList from '@/components/accounts/CapabilityList.vue'
import OAuthConnectButton from '@/components/accounts/OAuthConnectButton.vue'
import ConnectionErrorAlert from '@/components/accounts/ConnectionErrorAlert.vue'
import FolderTree from '@/components/accounts/FolderTree.vue'
import SafetyFields from '@/components/accounts/SafetyFields.vue'
import { safetySchema, type SafetySettings } from '@/components/accounts/safety'
import ServerSettingsFields from '@/components/accounts/ServerSettingsFields.vue'
import { emptyServerSettings, serverSettingsSchema, type ServerSettings } from '@/components/accounts/serverSettings'
import SpecialFoldersFields from '@/components/accounts/SpecialFoldersFields.vue'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { Separator } from '@/components/ui/separator'
import {
  Stepper,
  StepperDescription,
  StepperIndicator,
  StepperItem,
  StepperSeparator,
  StepperTitle,
} from '@/components/ui/stepper'
import { type DescribedError, useConnectionErrors } from '@/lib/connectionErrors'
import { useSchemaValidation } from '@/lib/zodForm'
import { type ConnectionTest, type MailProvider, type OAuthProvider, type SpecialFolders, useAccountsStore } from '@/stores/accounts'
import { CircleCheckIcon, Loader2Icon, MailIcon } from '@lucide/vue'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { toast } from 'vue-sonner'

type Step = 1 | 2 | 3 | 4

const { t } = useI18n()
const store = useAccountsStore()
const router = useRouter()
const { describe, describeRequestError } = useConnectionErrors()

const step = ref<Step>(1)
const provider = ref<MailProvider>('Imap')
const oauthProvider = computed(() => (provider.value === 'Imap' ? null : (provider.value as OAuthProvider)))
const steps = computed(() =>
  oauthProvider.value
    ? [
        { step: 1, title: t('accounts.wizard.stepProvider'), description: t('accounts.wizard.stepProviderDescription') },
        { step: 2, title: t('oauth.stepSignIn'), description: t('oauth.stepSignInDescription') },
      ]
    : [
        { step: 1, title: t('accounts.wizard.stepProvider'), description: t('accounts.wizard.stepProviderDescription') },
        { step: 2, title: t('accounts.wizard.stepServer'), description: t('accounts.wizard.stepServerDescription') },
        { step: 3, title: t('accounts.wizard.stepConnection'), description: t('accounts.wizard.stepConnectionDescription') },
        { step: 4, title: t('accounts.wizard.stepFolders'), description: t('accounts.wizard.stepFoldersDescription') },
      ],
)

// Google and Microsoft need an OAuth app registered by whoever runs this server (docs/oauth-setup.md).
const configured = ref<Partial<Record<MailProvider, boolean>>>({ Imap: true })
onMounted(async () => {
  try {
    for (const info of await store.oauthProviders()) configured.value[info.provider] = info.configured
  } catch {
    // Without the list, only IMAP is offered.
  }
})
const PROVIDERS: MailProvider[] = ['Imap', 'Google', 'Microsoft']
const oauthName = ref('')

// Step 2
const server = ref<ServerSettings>(emptyServerSettings())
const serverValidation = useSchemaValidation(computed(() => serverSettingsSchema(t, { passwordRequired: true })))

// Step 3
const testing = ref(false)
const test = ref<ConnectionTest | null>(null)
const testError = computed<DescribedError | null>(() =>
  test.value && !test.value.success ? describe(test.value.errorCode, test.value.errorMessage) : null,
)

// Step 4
const watched = ref<string[]>(['INBOX'])
const specialFolders = ref<SpecialFolders>({ sent: null, drafts: null, trash: null, junk: null, archive: null })
const safety = ref<SafetySettings>({ blastRadiusLimit: 200, purgeTrashAfterDays: null })
const safetyValidation = useSchemaValidation(computed(() => safetySchema(t)))
const watchedError = ref<string>()
const creating = ref(false)
const createError = ref<DescribedError | null>(null)

function choose(id: MailProvider) {
  provider.value = id
  step.value = 2
}

async function goToConnection() {
  if (!server.value.name.trim() && server.value.username.trim()) {
    server.value.name = server.value.username.trim()
  }
  if (!serverValidation.validate(server.value)) return
  step.value = 3
  await runTest()
}

async function runTest() {
  testing.value = true
  test.value = null
  try {
    const { host, port, security, username, password } = server.value
    test.value = await store.testSettings({ host, port, security, username, password })
    if (test.value.success && test.value.mailbox) {
      const mailbox = test.value.mailbox
      specialFolders.value = { ...mailbox.specialFolders }
      watched.value = [mailbox.folders.find((f) => f.specialUse === 'Inbox')?.path ?? 'INBOX']
    }
  } catch (error) {
    const described = describeRequestError(error)
    test.value = { success: false, errorCode: null, errorMessage: described.message, mailbox: null }
  } finally {
    testing.value = false
  }
}

async function create() {
  watchedError.value = watched.value.length === 0 ? t('validation.watchedRequired') : undefined
  const validSafety = safetyValidation.validate(safety.value)
  if (watchedError.value || !validSafety) return

  creating.value = true
  createError.value = null
  try {
    const { name, host, port, security, username, password } = server.value
    const account = await store.create({
      name: name.trim(),
      host,
      port,
      security,
      username,
      password,
      watchedFolders: watched.value,
      specialFolders: specialFolders.value,
      blastRadiusLimit: validSafety.blastRadiusLimit,
      purgeTrashAfterDays: validSafety.purgeTrashAfterDays,
    })
    toast.success(t('accounts.created'))
    await router.push({ name: 'account-detail', params: { id: account.id } })
  } catch (error) {
    createError.value = describeRequestError(error)
  } finally {
    creating.value = false
  }
}
</script>

<template>
  <div class="mx-auto grid max-w-3xl gap-6">
    <h2 class="text-2xl font-semibold tracking-tight">{{ $t('accounts.wizard.title') }}</h2>

    <Stepper :model-value="step" class="flex w-full items-start gap-2" :linear="true">
      <StepperItem
        v-for="item in steps"
        :key="item.step"
        :step="item.step"
        class="relative flex w-full flex-col items-center justify-center"
        :disabled="item.step > step"
      >
        <StepperSeparator
          v-if="item.step !== steps.length"
          class="bg-muted group-data-[state=completed]:bg-primary absolute top-4 right-[calc(-50%+20px)] left-[calc(50%+20px)] block h-0.5 shrink-0 rounded-full"
        />
        <StepperIndicator class="size-8">
          <CircleCheckIcon v-if="item.step < step" class="size-4" />
          <span v-else>{{ item.step }}</span>
        </StepperIndicator>
        <div class="mt-2 flex flex-col items-center text-center">
          <StepperTitle class="text-xs font-medium sm:text-sm">{{ item.title }}</StepperTitle>
          <StepperDescription class="hidden text-xs sm:block">{{ item.description }}</StepperDescription>
        </div>
      </StepperItem>
    </Stepper>

    <!-- Step 1: provider -->
    <div v-if="step === 1" class="grid gap-3 sm:grid-cols-3">
      <button
        v-for="id in PROVIDERS"
        :key="id"
        type="button"
        :disabled="!configured[id]"
        class="bg-card hover:bg-accent/50 focus-visible:ring-ring/50 grid gap-2 rounded-xl border p-4 text-left outline-none transition-colors focus-visible:ring-3 disabled:cursor-not-allowed disabled:opacity-60"
        :data-testid="`provider-${id}`"
        @click="choose(id)"
      >
        <span class="flex items-center justify-between gap-2">
          <span class="flex items-center gap-2 font-medium">
            <MailIcon class="size-4" />
            {{ $t(`accounts.provider.${id}`) }}
          </span>
          <Badge v-if="!configured[id]" variant="secondary">{{ $t('oauth.notConfigured') }}</Badge>
        </span>
        <span class="text-muted-foreground text-sm">
          {{ configured[id] ? $t(`accounts.provider.${id}Description`) : $t('oauth.notConfiguredHint') }}
        </span>
      </button>
    </div>

    <!-- Step 2 (Google / Microsoft): sign in with the provider -->
    <Card v-else-if="step === 2 && oauthProvider">
      <CardHeader>
        <CardTitle>{{ $t('oauth.signIn', { provider: $t(`accounts.provider.${oauthProvider}`) }) }}</CardTitle>
        <CardDescription>{{ $t('oauth.explanation', { provider: $t(`accounts.provider.${oauthProvider}`) }) }}</CardDescription>
      </CardHeader>
      <CardContent class="grid gap-2">
        <Label for="oauth-name">{{ $t('accounts.fields.name') }} ({{ $t('common.optional') }})</Label>
        <Input id="oauth-name" v-model="oauthName" :placeholder="$t('oauth.namePlaceholder')" autocomplete="off" class="max-w-sm" />
      </CardContent>
      <CardFooter class="mt-6 flex justify-between">
        <Button type="button" variant="outline" @click="step = 1">{{ $t('common.back') }}</Button>
        <OAuthConnectButton :provider="oauthProvider" :name="oauthName.trim() || undefined" />
      </CardFooter>
    </Card>

    <!-- Step 2: server settings -->
    <Card v-else-if="step === 2">
      <CardHeader>
        <CardTitle>{{ $t('accounts.wizard.stepServer') }}</CardTitle>
        <CardDescription>{{ $t('accounts.provider.ImapDescription') }}</CardDescription>
      </CardHeader>
      <form novalidate @submit.prevent="goToConnection">
        <CardContent>
          <ServerSettingsFields v-model="server" :errors="serverValidation.errors.value" />
        </CardContent>
        <CardFooter class="mt-6 flex justify-between">
          <Button type="button" variant="outline" @click="step = 1">{{ $t('common.back') }}</Button>
          <Button type="submit">{{ $t('common.next') }}</Button>
        </CardFooter>
      </form>
    </Card>

    <!-- Step 3: connection test -->
    <Card v-else-if="step === 3">
      <CardHeader>
        <CardTitle>{{ $t('accounts.wizard.stepConnection') }}</CardTitle>
      </CardHeader>
      <CardContent class="grid gap-4">
        <p v-if="testing" class="text-muted-foreground flex items-center gap-2" role="status">
          <Loader2Icon class="size-4 animate-spin" />
          {{ $t('accounts.wizard.connecting', { host: server.host }) }}
        </p>
        <ConnectionErrorAlert v-else-if="testError" :title="$t('accounts.testFailed')" :error="testError" />
        <template v-else-if="test?.mailbox">
          <p class="text-success flex items-center gap-2 font-medium" data-testid="connection-ok">
            <CircleCheckIcon class="size-5" />
            {{ $t('accounts.wizard.connectionOk', { host: server.host }) }}
          </p>
          <p class="text-muted-foreground text-sm">
            {{ $t('accounts.wizard.foldersFound', { count: test.mailbox.folders.length }, test.mailbox.folders.length) }}
          </p>
          <Separator />
          <div class="grid gap-2">
            <h3 class="text-sm font-medium">{{ $t('accounts.overview.capabilities') }}</h3>
            <CapabilityList :capabilities="test.mailbox.capabilities.all" />
          </div>
        </template>
      </CardContent>
      <CardFooter class="mt-6 flex justify-between">
        <Button type="button" variant="outline" :disabled="testing" @click="step = 2">{{ $t('common.back') }}</Button>
        <div class="flex gap-2">
          <Button v-if="testError" type="button" variant="outline" @click="runTest">{{ $t('common.retry') }}</Button>
          <Button type="button" :disabled="testing || !test?.success" @click="step = 4">{{ $t('common.next') }}</Button>
        </div>
      </CardFooter>
    </Card>

    <!-- Step 4: folders and safety -->
    <template v-else-if="step === 4 && test?.mailbox">
      <Card>
        <CardHeader>
          <CardTitle>{{ $t('accounts.folders.watched') }}</CardTitle>
          <CardDescription>{{ $t('accounts.folders.watchedHint') }}</CardDescription>
        </CardHeader>
        <CardContent class="grid gap-2">
          <div class="max-h-96 overflow-y-auto">
            <FolderTree v-model="watched" :folders="test.mailbox.folders" selectable />
          </div>
          <p v-if="watchedError" class="text-destructive text-sm" role="alert">{{ watchedError }}</p>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>{{ $t('accounts.folders.specialTitle') }}</CardTitle>
          <CardDescription>{{ $t('accounts.folders.specialHint') }}</CardDescription>
        </CardHeader>
        <CardContent>
          <SpecialFoldersFields v-model="specialFolders" :folders="test.mailbox.folders" />
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>{{ $t('accounts.safety.title') }}</CardTitle>
        </CardHeader>
        <CardContent>
          <SafetyFields v-model="safety" :errors="safetyValidation.errors.value" />
        </CardContent>
      </Card>

      <ConnectionErrorAlert v-if="createError" :title="$t('accounts.saveFailed')" :error="createError" />

      <div class="flex justify-between">
        <Button type="button" variant="outline" :disabled="creating" @click="step = 3">{{ $t('common.back') }}</Button>
        <Button type="button" :disabled="creating" data-testid="create-account" @click="create">
          <Loader2Icon v-if="creating" class="animate-spin" />
          {{ $t('accounts.wizard.create') }}
        </Button>
      </div>
    </template>
  </div>
</template>
