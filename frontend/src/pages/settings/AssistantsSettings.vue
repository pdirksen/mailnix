<script setup lang="ts">
import FormField from '@/components/common/FormField.vue'
import CopyField from '@/components/settings/CopyField.vue'
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from '@/components/ui/alert-dialog'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { RadioGroup, RadioGroupItem } from '@/components/ui/radio-group'
import { Skeleton } from '@/components/ui/skeleton'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { useConnectionErrors } from '@/lib/connectionErrors'
import { formatDateTime, formatRelative } from '@/lib/format'
import { mcpClients, mcpSnippet } from '@/lib/mcpConfig'
import { type ApiScope, type ApiToken, type CreatedApiToken, useAssistantsStore } from '@/stores/assistants'
import type { Profile } from '@/stores/profile'
import { BotIcon, KeyRoundIcon, Loader2Icon, PlugIcon, PlusIcon, TrashIcon, TriangleAlertIcon } from '@lucide/vue'
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { toast } from 'vue-sonner'

// Passed to every settings section by the layout; not needed here.
defineProps<{ profile: Profile }>()

const { t, locale } = useI18n()
const store = useAssistantsStore()
const { describeRequestError } = useConnectionErrors()

const failure = ref<string>()
async function load() {
  failure.value = undefined
  try {
    await store.load()
  } catch (error) {
    failure.value = describeRequestError(error).message
  }
}
onMounted(load)

// Creating a token
const EXPIRY_OPTIONS = ['30', '90', '365', 'never'] as const
const SCOPES: ApiScope[] = ['Read', 'ReadWrite']
const createOpen = ref(false)
const name = ref('')
const scope = ref<ApiScope>('Read')
const expiry = ref<(typeof EXPIRY_OPTIONS)[number]>('90')
const nameError = ref<string>()
const creating = ref(false)
const created = ref<CreatedApiToken>()

function openCreate() {
  name.value = ''
  scope.value = 'Read'
  expiry.value = '90'
  nameError.value = undefined
  created.value = undefined
  createOpen.value = true
}

async function create() {
  nameError.value = name.value.trim() ? undefined : t('validation.required')
  if (nameError.value) return
  creating.value = true
  try {
    created.value = await store.create(name.value.trim(), scope.value, expiry.value === 'never' ? null : Number(expiry.value))
  } catch (error) {
    toast.error(describeRequestError(error).message)
  } finally {
    creating.value = false
  }
}

// Revoking (the dialog closes before its action runs, so the token is kept apart from the open state)
const revoking = ref<ApiToken>()
const revokeOpen = ref(false)
function confirmRevoke(token: ApiToken) {
  revoking.value = token
  revokeOpen.value = true
}

async function revoke() {
  if (!revoking.value) return
  const token = revoking.value
  try {
    await store.revoke(token.id)
    toast.success(t(token.kind === 'Connection' ? 'settings.assistants.disconnected' : 'settings.assistants.revoked', { name: token.name }))
  } catch (error) {
    toast.error(describeRequestError(error).message)
  }
}
</script>

<template>
  <div class="grid gap-6">
    <!-- Connecting an app through sign-in (OAuth) -->
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2"><BotIcon class="size-4" />{{ $t('settings.assistants.title') }}</CardTitle>
        <CardDescription>{{ $t('settings.assistants.intro') }}</CardDescription>
      </CardHeader>
      <CardContent class="grid gap-4">
        <div class="grid gap-2">
          <Label>{{ $t('settings.assistants.serverAddress') }}</Label>
          <Skeleton v-if="!store.loaded && !failure" class="h-11 rounded-lg" />
          <CopyField v-else-if="store.mcpUrl" :value="store.mcpUrl" :label="$t('settings.assistants.copyAddress')" data-testid="mcp-url" />
        </div>
        <div class="text-muted-foreground grid gap-1 text-sm">
          <p class="text-foreground font-medium">{{ $t('settings.assistants.connectorTitle') }}</p>
          <p>{{ $t('settings.assistants.connectorSteps') }}</p>
        </div>
        <div class="text-muted-foreground flex gap-2 rounded-lg border p-3 text-sm">
          <TriangleAlertIcon class="mt-0.5 size-4 shrink-0" />
          <p>{{ $t('settings.assistants.safety') }}</p>
        </div>
      </CardContent>
    </Card>

    <!-- Tokens and connected apps -->
    <Card>
      <CardHeader class="flex flex-row items-start justify-between gap-4">
        <div class="grid gap-1.5">
          <CardTitle class="flex items-center gap-2"><KeyRoundIcon class="size-4" />{{ $t('settings.assistants.accessTitle') }}</CardTitle>
          <CardDescription>{{ $t('settings.assistants.accessHint') }}</CardDescription>
        </div>
        <Button size="sm" data-testid="create-token" @click="openCreate"><PlusIcon />{{ $t('settings.assistants.createToken') }}</Button>
      </CardHeader>
      <CardContent>
        <div v-if="failure" class="grid justify-items-start gap-3">
          <p class="text-sm">{{ failure }}</p>
          <Button variant="outline" size="sm" @click="load">{{ $t('common.retry') }}</Button>
        </div>
        <Skeleton v-else-if="!store.loaded" class="h-24 rounded-lg" />
        <p v-else-if="store.tokens.length === 0" class="text-muted-foreground text-sm" data-testid="tokens-empty">
          {{ $t('settings.assistants.empty') }}
        </p>
        <ul v-else class="divide-y rounded-lg border" role="list" data-testid="token-list">
          <li v-for="token in store.tokens" :key="token.id" class="flex flex-wrap items-center gap-x-4 gap-y-2 p-3">
            <component :is="token.kind === 'Connection' ? PlugIcon : KeyRoundIcon" class="text-muted-foreground size-4 shrink-0" />
            <div class="grid min-w-0 flex-1 gap-1">
              <div class="flex flex-wrap items-center gap-2">
                <span class="truncate font-medium">{{ token.name }}</span>
                <Badge variant="outline">{{ $t(`settings.assistants.kinds.${token.kind}`) }}</Badge>
                <Badge :variant="token.scope === 'ReadWrite' ? 'default' : 'secondary'">{{ $t(`settings.assistants.scopes.${token.scope}`) }}</Badge>
                <Badge v-if="token.expired" variant="destructive">{{ $t('settings.assistants.expired') }}</Badge>
              </div>
              <p class="text-muted-foreground text-xs">
                <span v-if="token.tokenHint" class="font-mono">{{ token.tokenHint }}… · </span>
                {{
                  token.lastUsedAt
                    ? $t('settings.assistants.lastUsed', { time: formatRelative(token.lastUsedAt, locale) })
                    : $t('settings.assistants.neverUsed')
                }}
                ·
                {{
                  token.expiresAt
                    ? $t('settings.assistants.expires', { time: formatDateTime(token.expiresAt, locale) })
                    : $t('settings.assistants.noExpiry')
                }}
              </p>
            </div>
            <Button
              variant="ghost"
              size="sm"
              class="text-destructive hover:text-destructive"
              :data-testid="`revoke-${token.id}`"
              @click="confirmRevoke(token)"
            >
              <TrashIcon />{{ token.kind === 'Connection' ? $t('settings.assistants.disconnect') : $t('settings.assistants.revoke') }}
            </Button>
          </li>
        </ul>
      </CardContent>
    </Card>

    <Dialog v-model:open="createOpen">
      <DialogContent class="grid-cols-[minmax(0,1fr)] sm:max-w-xl">
        <template v-if="!created">
          <DialogHeader>
            <DialogTitle>{{ $t('settings.assistants.createTitle') }}</DialogTitle>
            <DialogDescription>{{ $t('settings.assistants.createHint') }}</DialogDescription>
          </DialogHeader>
          <form id="create-token-form" class="grid gap-5" novalidate @submit.prevent="create">
            <FormField id="token-name" v-slot="aria" :label="$t('common.name')" :error="nameError">
              <Input id="token-name" v-model="name" v-bind="aria" maxlength="100" :placeholder="$t('settings.assistants.namePlaceholder')" />
            </FormField>
            <fieldset class="grid gap-3">
              <legend class="mb-2 text-sm font-medium">{{ $t('settings.assistants.scopeLabel') }}</legend>
              <RadioGroup v-model="scope" class="grid gap-3">
                <div v-for="option in SCOPES" :key="option" class="flex items-start gap-3">
                  <RadioGroupItem :id="`scope-${option}`" :value="option" class="mt-0.5" />
                  <Label :for="`scope-${option}`" class="grid gap-1 font-normal">
                    <span class="font-medium">{{ $t(`settings.assistants.scopes.${option}`) }}</span>
                    <span class="text-muted-foreground text-xs">{{ $t(`settings.assistants.scopeHints.${option}`) }}</span>
                  </Label>
                </div>
              </RadioGroup>
            </fieldset>
            <fieldset class="grid gap-3">
              <legend class="mb-2 text-sm font-medium">{{ $t('settings.assistants.expiryLabel') }}</legend>
              <RadioGroup v-model="expiry" class="flex flex-wrap gap-x-5 gap-y-2">
                <div v-for="option in EXPIRY_OPTIONS" :key="option" class="flex items-center gap-2">
                  <RadioGroupItem :id="`expiry-${option}`" :value="option" />
                  <Label :for="`expiry-${option}`" class="font-normal">{{ $t(`settings.assistants.expiry.${option}`) }}</Label>
                </div>
              </RadioGroup>
            </fieldset>
          </form>
          <DialogFooter>
            <Button variant="outline" @click="createOpen = false">{{ $t('common.cancel') }}</Button>
            <Button type="submit" form="create-token-form" :disabled="creating" data-testid="create-token-submit">
              <Loader2Icon v-if="creating" class="animate-spin" />
              {{ $t('common.create') }}
            </Button>
          </DialogFooter>
        </template>

        <template v-else>
          <DialogHeader>
            <DialogTitle>{{ $t('settings.assistants.createdTitle') }}</DialogTitle>
            <DialogDescription>{{ $t('settings.assistants.createdHint') }}</DialogDescription>
          </DialogHeader>
          <div class="grid min-w-0 grid-cols-[minmax(0,1fr)] gap-4">
            <CopyField :value="created.secret" :label="$t('settings.assistants.copyToken')" data-testid="created-token" />
            <Tabs default-value="claudeCode" class="min-w-0">
              <TabsList>
                <TabsTrigger v-for="client in mcpClients" :key="client" :value="client">{{ $t(`settings.assistants.clients.${client}`) }}</TabsTrigger>
              </TabsList>
              <TabsContent v-for="client in mcpClients" :key="client" :value="client" class="grid min-w-0 grid-cols-[minmax(0,1fr)] gap-2">
                <p class="text-muted-foreground text-xs">{{ $t(`settings.assistants.clientHints.${client}`) }}</p>
                <CopyField
                  :value="mcpSnippet(client, created.mcpUrl, created.secret)"
                  :label="$t('settings.assistants.copySnippet')"
                  :multiline="client !== 'claudeCode'"
                  :data-testid="`snippet-${client}`"
                />
              </TabsContent>
            </Tabs>
          </div>
          <DialogFooter>
            <Button @click="createOpen = false">{{ $t('settings.assistants.done') }}</Button>
          </DialogFooter>
        </template>
      </DialogContent>
    </Dialog>

    <AlertDialog v-model:open="revokeOpen">
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>
            {{ $t(revoking?.kind === 'Connection' ? 'settings.assistants.disconnectTitle' : 'settings.assistants.revokeTitle', { name: revoking?.name }) }}
          </AlertDialogTitle>
          <AlertDialogDescription>
            {{ $t(revoking?.kind === 'Connection' ? 'settings.assistants.disconnectConfirm' : 'settings.assistants.revokeConfirm') }}
          </AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel>{{ $t('common.cancel') }}</AlertDialogCancel>
          <AlertDialogAction data-testid="confirm-revoke" @click="revoke">
            {{ revoking?.kind === 'Connection' ? $t('settings.assistants.disconnect') : $t('settings.assistants.revoke') }}
          </AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  </div>
</template>
