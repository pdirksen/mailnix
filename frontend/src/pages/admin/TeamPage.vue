<script setup lang="ts">
import { api, ApiError, type Schemas, unwrap } from '@/api/client'
import FormField from '@/components/common/FormField.vue'
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
  AlertDialogTrigger,
} from '@/components/ui/alert-dialog'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { Switch } from '@/components/ui/switch'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { useConnectionErrors } from '@/lib/connectionErrors'
import { formatDateTime } from '@/lib/format'
import { useAuthStore } from '@/stores/auth'
import { CopyIcon, Loader2Icon, MailPlusIcon, Trash2Icon, XIcon } from '@lucide/vue'
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { toast } from 'vue-sonner'

type Team = Schemas['TeamResponse']
type Member = Schemas['TeamMember']

const { t, te, locale } = useI18n()
const auth = useAuthStore()
const { describeRequestError } = useConnectionErrors()

const team = ref<Team>()
const loading = ref(true)

/** Team errors have their own texts (e.g. "someone has to stay administrator"). */
function showError(error: unknown) {
  const key = `team.errors.${error instanceof ApiError ? error.code : ''}`
  toast.error(te(key) ? t(key) : describeRequestError(error).message)
}

async function load() {
  try {
    team.value = unwrap(await api.GET('/api/team'))
    organizationName.value = team.value.tenant.name
  } catch (error) {
    showError(error)
  } finally {
    loading.value = false
  }
}

onMounted(load)

// Organization name
const organizationName = ref('')
const renaming = ref(false)
const nameChanged = computed(() => !!team.value && organizationName.value.trim() !== team.value.tenant.name)

async function rename() {
  if (!organizationName.value.trim()) return
  renaming.value = true
  try {
    unwrap(await api.PUT('/api/team', { body: { name: organizationName.value.trim() } }))
    toast.success(t('team.renamed'))
    await load()
  } catch (error) {
    showError(error)
  } finally {
    renaming.value = false
  }
}

const usage = computed(() => {
  const tenant = team.value?.tenant
  if (!tenant) return ''
  return tenant.maxAccounts == null
    ? t('team.mailboxes', { count: tenant.accounts }, tenant.accounts)
    : t('team.mailboxesOfLimit', { count: tenant.accounts, max: tenant.maxAccounts })
})

// Members
async function setTenantAdmin(member: Member, value: boolean) {
  try {
    unwrap(await api.PUT('/api/team/users/{id}/admin', { params: { path: { id: member.id } }, body: { isTenantAdmin: value } }))
    member.isTenantAdmin = value
  } catch (error) {
    showError(error)
  }
}

async function remove(member: Member) {
  try {
    unwrap(await api.DELETE('/api/team/users/{id}', { params: { path: { id: member.id } } }))
    toast.success(t('team.removed', { email: member.email }))
    await load()
  } catch (error) {
    showError(error)
  }
}

// Invitations
const inviteEmail = ref('')
const inviting = ref(false)
/** The link of the invitation just created; the server shows it only once. */
const createdLink = ref<string>()

/** Send the link by email as well (when the server can send email and an address is given). */
const sendEmail = ref(true)
const canEmail = computed(() => !!team.value?.emailAvailable && inviteEmail.value.trim() !== '')

async function invite() {
  inviting.value = true
  try {
    const email = inviteEmail.value.trim() || null
    const created = unwrap(await api.POST('/api/team/invitations', { body: { email, sendEmail: canEmail.value && sendEmail.value } }))
    createdLink.value = created.url
    if (created.emailSent) toast.success(t('team.emailSent', { email }))
    inviteEmail.value = ''
    await load()
  } catch (error) {
    showError(error)
  } finally {
    inviting.value = false
  }
}

async function copyLink() {
  if (!createdLink.value) return
  try {
    await navigator.clipboard.writeText(createdLink.value)
    toast.success(t('team.linkCopied'))
  } catch {
    toast.error(t('team.copyFailed'))
  }
}

async function revoke(invitation: Schemas['InvitationItem']) {
  try {
    unwrap(await api.DELETE('/api/team/invitations/{id}', { params: { path: { id: invitation.id } } }))
    await load()
  } catch (error) {
    showError(error)
  }
}
</script>

<template>
  <div class="mx-auto grid max-w-5xl gap-6">
    <div>
      <h2 class="text-2xl font-semibold tracking-tight">{{ $t('team.title') }}</h2>
      <p class="text-muted-foreground mt-1 max-w-2xl">{{ $t('team.subtitle') }}</p>
    </div>

    <template v-if="loading && !team">
      <Skeleton class="h-36 rounded-xl" />
      <Skeleton class="h-64 rounded-xl" />
    </template>

    <template v-else-if="team">
      <Card>
        <CardHeader>
          <CardTitle>{{ $t('team.organization') }}</CardTitle>
          <CardDescription data-testid="mailbox-usage">{{ usage }}</CardDescription>
        </CardHeader>
        <CardContent>
          <form novalidate class="flex flex-wrap items-end gap-3" @submit.prevent="rename">
            <FormField id="organization-name" v-slot="aria" :label="$t('common.name')" class="min-w-60 flex-1">
              <Input id="organization-name" v-model="organizationName" v-bind="aria" maxlength="200" autocomplete="organization" />
            </FormField>
            <Button type="submit" variant="outline" :disabled="!nameChanged || !organizationName.trim() || renaming">
              <Loader2Icon v-if="renaming" class="animate-spin" />
              {{ $t('common.save') }}
            </Button>
          </form>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>{{ $t('team.members') }}</CardTitle>
          <CardDescription>{{ $t('team.membersHint') }}</CardDescription>
        </CardHeader>
        <CardContent class="overflow-x-auto">
          <Table data-testid="team-members">
            <TableHeader>
              <TableRow>
                <TableHead>{{ $t('team.member') }}</TableHead>
                <TableHead class="text-right">{{ $t('team.mailboxesColumn') }}</TableHead>
                <TableHead>{{ $t('team.joined') }}</TableHead>
                <TableHead>{{ $t('team.organizationAdmin') }}</TableHead>
                <TableHead><span class="sr-only">{{ $t('common.actions') }}</span></TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              <TableRow v-for="member in team.users" :key="member.id" :data-testid="`member-${member.email}`">
                <TableCell class="max-w-72">
                  <div class="truncate font-medium">{{ member.displayName || member.email }}</div>
                  <div v-if="member.displayName" class="text-muted-foreground truncate text-xs">{{ member.email }}</div>
                  <div class="mt-1 flex flex-wrap gap-1">
                    <Badge v-if="member.id === auth.user?.id" variant="secondary">{{ $t('team.you') }}</Badge>
                    <Badge v-if="member.isAdmin" variant="outline">{{ $t('team.installationAdmin') }}</Badge>
                  </div>
                </TableCell>
                <TableCell class="text-right tabular-nums">{{ member.accounts }}</TableCell>
                <TableCell class="whitespace-nowrap">{{ formatDateTime(member.createdAt, locale) }}</TableCell>
                <TableCell>
                  <Switch
                    :model-value="member.isTenantAdmin"
                    :aria-label="$t('team.organizationAdminFor', { email: member.email })"
                    :data-testid="`admin-${member.email}`"
                    @update:model-value="(value) => setTenantAdmin(member, value)"
                  />
                </TableCell>
                <TableCell class="text-right">
                  <AlertDialog v-if="member.id !== auth.user?.id && !member.isAdmin">
                    <AlertDialogTrigger as-child>
                      <Button variant="ghost" size="icon" :aria-label="$t('team.remove', { email: member.email })">
                        <Trash2Icon />
                      </Button>
                    </AlertDialogTrigger>
                    <AlertDialogContent>
                      <AlertDialogHeader>
                        <AlertDialogTitle>{{ $t('team.remove', { email: member.email }) }}</AlertDialogTitle>
                        <AlertDialogDescription>{{ $t('team.removeText', { count: member.accounts }, member.accounts) }}</AlertDialogDescription>
                      </AlertDialogHeader>
                      <AlertDialogFooter>
                        <AlertDialogCancel>{{ $t('common.cancel') }}</AlertDialogCancel>
                        <AlertDialogAction class="bg-destructive hover:bg-destructive/90 text-white" @click="remove(member)">
                          {{ $t('team.removeConfirm') }}
                        </AlertDialogAction>
                      </AlertDialogFooter>
                    </AlertDialogContent>
                  </AlertDialog>
                </TableCell>
              </TableRow>
            </TableBody>
          </Table>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>{{ $t('team.invitations') }}</CardTitle>
          <CardDescription>{{ $t('team.invitationsHint') }}</CardDescription>
        </CardHeader>
        <CardContent class="grid gap-4">
          <form novalidate class="flex flex-wrap items-end gap-3" @submit.prevent="invite">
            <FormField id="invite-email" v-slot="aria" :label="$t('team.inviteEmail')" :hint="$t('team.inviteEmailHint')" class="min-w-60 flex-1">
              <Input id="invite-email" v-model="inviteEmail" v-bind="aria" type="email" autocomplete="off" />
            </FormField>
            <Button type="submit" :disabled="inviting" data-testid="create-invitation">
              <Loader2Icon v-if="inviting" class="animate-spin" />
              <MailPlusIcon v-else />
              {{ $t('team.invite') }}
            </Button>
            <div v-if="canEmail" class="flex basis-full items-center gap-2">
              <Checkbox id="invite-send-email" v-model="sendEmail" />
              <Label for="invite-send-email" class="font-normal">{{ $t('team.sendByEmail') }}</Label>
            </div>
          </form>

          <div v-if="createdLink" class="bg-muted/50 grid gap-2 rounded-lg border p-3" data-testid="invitation-link">
            <p class="text-sm font-medium">{{ $t('team.linkTitle') }}</p>
            <div class="flex gap-2">
              <Input :model-value="createdLink" readonly :aria-label="$t('team.linkTitle')" class="font-mono text-xs" @focus="($event.target as HTMLInputElement).select()" />
              <Button type="button" variant="outline" @click="copyLink">
                <CopyIcon />
                {{ $t('team.copy') }}
              </Button>
            </div>
            <p class="text-muted-foreground text-xs">{{ $t('team.linkHint') }}</p>
          </div>

          <p v-if="team.invitations.length === 0" class="text-muted-foreground text-sm">{{ $t('team.noInvitations') }}</p>
          <ul v-else class="divide-y rounded-lg border" role="list" data-testid="invitations">
            <li v-for="invitation in team.invitations" :key="invitation.id" class="flex items-center gap-3 px-3 py-2">
              <div class="min-w-0 flex-1">
                <div class="truncate text-sm font-medium">{{ invitation.email ?? $t('team.anyone') }}</div>
                <div class="text-muted-foreground text-xs">{{ $t('team.expires', { time: formatDateTime(invitation.expiresAt, locale) }) }}</div>
              </div>
              <Button variant="ghost" size="sm" @click="revoke(invitation)">
                <XIcon />
                {{ $t('team.revoke') }}
              </Button>
            </li>
          </ul>
        </CardContent>
      </Card>
    </template>
  </div>
</template>
