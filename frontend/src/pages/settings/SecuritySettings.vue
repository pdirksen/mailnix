<script setup lang="ts">
import { ApiError } from '@/api/client'
import FormField from '@/components/common/FormField.vue'
import PasswordStrength from '@/components/common/PasswordStrength.vue'
import PasswordPromptDialog from '@/components/settings/PasswordPromptDialog.vue'
import RecoveryCodes from '@/components/settings/RecoveryCodes.vue'
import TwoFactorSetupDialog from '@/components/settings/TwoFactorSetupDialog.vue'
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
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { useConnectionErrors } from '@/lib/connectionErrors'
import { useAuthStore } from '@/stores/auth'
import { type Profile, useProfileStore } from '@/stores/profile'
import { KeyRoundIcon, Loader2Icon, LogOutIcon, ShieldAlertIcon, ShieldCheckIcon, SmartphoneIcon, TriangleAlertIcon } from '@lucide/vue'
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { toast } from 'vue-sonner'

/** Must match the server's password policy (Identity RequiredLength). */
const MIN_PASSWORD_LENGTH = 10

const props = defineProps<{ profile: Profile }>()

const { t, te } = useI18n()
const auth = useAuthStore()
const store = useProfileStore()
const router = useRouter()
const { describeRequestError } = useConnectionErrors()

function message(error: unknown) {
  const key = `settings.errors.${error instanceof ApiError ? error.code : ''}`
  return te(key) ? t(key) : describeRequestError(error).message
}

// Password
const current = ref('')
const next = ref('')
const repeat = ref('')
const passwordErrors = ref<{ current?: string; next?: string; repeat?: string }>({})
const changingPassword = ref(false)

async function changePassword() {
  passwordErrors.value = {
    current: current.value ? undefined : t('validation.required'),
    next:
      next.value.length < MIN_PASSWORD_LENGTH
        ? t('validation.passwordLength', { min: MIN_PASSWORD_LENGTH })
        : next.value === current.value
          ? t('settings.security.samePassword')
          : undefined,
    repeat: repeat.value === next.value ? undefined : t('validation.passwordMismatch'),
  }
  if (Object.values(passwordErrors.value).some(Boolean)) return
  changingPassword.value = true
  try {
    await store.changePassword(current.value, next.value)
    current.value = next.value = repeat.value = ''
    toast.success(t('settings.security.passwordChanged'))
  } catch (error) {
    if (error instanceof ApiError && error.code === 'WrongPassword') passwordErrors.value = { current: t('settings.errors.WrongPassword') }
    else toast.error(message(error))
  } finally {
    changingPassword.value = false
  }
}

// Two-factor sign-in
const setupOpen = ref(false)
const disableOpen = ref(false)
const renewOpen = ref(false)
const renewedCodes = ref<string[]>([])
const codesOpen = ref(false)
const twoFactor = computed(() => props.profile.twoFactor)

async function disableTwoFactor(password: string) {
  await store.disableTwoFactor(password)
  toast.success(t('settings.twoFactor.turnedOff'))
}

async function renewCodes(password: string) {
  renewedCodes.value = await store.newRecoveryCodes(password)
  codesOpen.value = true
}

// Other devices
const signingOut = ref(false)
async function signOutOthers() {
  signingOut.value = true
  try {
    await store.signOutOthers()
    toast.success(t('settings.security.othersSignedOut'))
  } catch (error) {
    toast.error(message(error))
  } finally {
    signingOut.value = false
  }
}

// Delete account
const deleteOpen = ref(false)
const confirmText = ref('')
const deleteWord = computed(() => t('settings.danger.confirmWord'))
async function deleteAccount(password: string) {
  if (confirmText.value.trim().toLowerCase() !== deleteWord.value.toLowerCase()) throw new Error(t('settings.danger.typeToConfirm', { word: deleteWord.value }))
  await store.deleteAccount(password)
  await router.replace({ name: 'login' })
  toast.success(t('settings.danger.deleted'))
}
</script>

<template>
  <div class="grid gap-6">
    <!-- Password -->
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2"><KeyRoundIcon class="size-4" />{{ $t('settings.security.passwordTitle') }}</CardTitle>
        <CardDescription>{{ $t('settings.security.passwordHint') }}</CardDescription>
      </CardHeader>
      <form novalidate @submit.prevent="changePassword">
        <CardContent class="grid gap-4 sm:max-w-md">
          <input type="email" :value="profile.email" autocomplete="username" class="hidden" readonly aria-hidden="true" tabindex="-1" />
          <FormField id="current-password" v-slot="aria" :label="$t('settings.security.currentPassword')" :error="passwordErrors.current">
            <Input id="current-password" v-model="current" v-bind="aria" type="password" autocomplete="current-password" />
          </FormField>
          <FormField id="new-password" v-slot="aria" :label="$t('settings.security.newPassword')" :error="passwordErrors.next">
            <Input id="new-password" v-model="next" v-bind="aria" type="password" autocomplete="new-password" />
          </FormField>
          <PasswordStrength :password="next" />
          <FormField id="repeat-password" v-slot="aria" :label="$t('auth.confirmPassword')" :error="passwordErrors.repeat">
            <Input id="repeat-password" v-model="repeat" v-bind="aria" type="password" autocomplete="new-password" />
          </FormField>
        </CardContent>
        <CardFooter class="mt-6 justify-end border-t pt-6">
          <Button type="submit" :disabled="changingPassword" data-testid="change-password">
            <Loader2Icon v-if="changingPassword" class="animate-spin" />
            {{ $t('settings.security.changePassword') }}
          </Button>
        </CardFooter>
      </form>
    </Card>

    <!-- Two-factor sign-in -->
    <Card data-testid="two-factor-card">
      <CardHeader>
        <div class="flex flex-wrap items-center justify-between gap-2">
          <CardTitle class="flex items-center gap-2"><SmartphoneIcon class="size-4" />{{ $t('settings.twoFactor.title') }}</CardTitle>
          <Badge :variant="twoFactor.enabled ? 'default' : 'outline'" class="gap-1" data-testid="two-factor-status">
            <ShieldCheckIcon v-if="twoFactor.enabled" class="size-3" />
            {{ twoFactor.enabled ? $t('settings.twoFactor.on') : $t('settings.twoFactor.off') }}
          </Badge>
        </div>
        <CardDescription>{{ $t('settings.twoFactor.description') }}</CardDescription>
      </CardHeader>
      <CardContent class="grid gap-4">
        <template v-if="twoFactor.enabled">
          <div
            class="flex items-start gap-3 rounded-lg border px-3 py-2.5 text-sm"
            :class="twoFactor.recoveryCodesLeft <= 3 ? 'border-amber-500/50 bg-amber-500/10' : ''"
          >
            <ShieldAlertIcon v-if="twoFactor.recoveryCodesLeft <= 3" class="mt-0.5 size-4 shrink-0 text-amber-600" />
            <span>
              {{ $t('settings.twoFactor.codesLeft', { count: twoFactor.recoveryCodesLeft }, twoFactor.recoveryCodesLeft) }}
              <template v-if="twoFactor.recoveryCodesLeft <= 3"> {{ $t('settings.twoFactor.codesLow') }}</template>
            </span>
          </div>
          <div class="flex flex-wrap gap-2">
            <Button variant="outline" @click="renewOpen = true">{{ $t('settings.twoFactor.newCodes') }}</Button>
            <Button variant="ghost" class="text-destructive hover:text-destructive" @click="disableOpen = true">
              {{ $t('settings.twoFactor.turnOff') }}
            </Button>
          </div>
        </template>
        <div v-else>
          <Button data-testid="two-factor-setup" @click="setupOpen = true">
            <ShieldCheckIcon />
            {{ $t('settings.twoFactor.setUp') }}
          </Button>
        </div>
      </CardContent>
    </Card>

    <!-- Other devices -->
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2"><LogOutIcon class="size-4" />{{ $t('settings.security.sessionsTitle') }}</CardTitle>
        <CardDescription>{{ $t('settings.security.sessionsHint') }}</CardDescription>
      </CardHeader>
      <CardContent>
        <AlertDialog>
          <AlertDialogTrigger as-child>
            <Button variant="outline" :disabled="signingOut">
              <Loader2Icon v-if="signingOut" class="animate-spin" />
              {{ $t('settings.security.signOutOthers') }}
            </Button>
          </AlertDialogTrigger>
          <AlertDialogContent>
            <AlertDialogHeader>
              <AlertDialogTitle>{{ $t('settings.security.signOutOthers') }}</AlertDialogTitle>
              <AlertDialogDescription>{{ $t('settings.security.signOutOthersConfirm') }}</AlertDialogDescription>
            </AlertDialogHeader>
            <AlertDialogFooter>
              <AlertDialogCancel>{{ $t('common.cancel') }}</AlertDialogCancel>
              <AlertDialogAction @click="signOutOthers">{{ $t('settings.security.signOutOthers') }}</AlertDialogAction>
            </AlertDialogFooter>
          </AlertDialogContent>
        </AlertDialog>
      </CardContent>
    </Card>

    <!-- Danger zone -->
    <Card class="border-destructive/40">
      <CardHeader>
        <CardTitle class="text-destructive flex items-center gap-2"><TriangleAlertIcon class="size-4" />{{ $t('settings.danger.title') }}</CardTitle>
        <CardDescription>
          {{ auth.isAdmin ? $t('settings.danger.adminCannot') : $t('settings.danger.description') }}
        </CardDescription>
      </CardHeader>
      <CardContent v-if="!auth.isAdmin">
        <Button variant="destructive" data-testid="delete-account" @click="confirmText = ''; deleteOpen = true">
          {{ $t('settings.danger.delete') }}
        </Button>
      </CardContent>
    </Card>

    <TwoFactorSetupDialog v-model:open="setupOpen" />

    <PasswordPromptDialog
      v-model:open="disableOpen"
      :title="$t('settings.twoFactor.turnOffTitle')"
      :description="$t('settings.twoFactor.turnOffHint')"
      :confirm-label="$t('settings.twoFactor.turnOff')"
      destructive
      :action="disableTwoFactor"
    />

    <PasswordPromptDialog
      v-model:open="renewOpen"
      :title="$t('settings.twoFactor.newCodes')"
      :description="$t('settings.twoFactor.newCodesHint')"
      :confirm-label="$t('settings.twoFactor.createCodes')"
      :action="renewCodes"
    />
    <Dialog v-model:open="codesOpen">
      <DialogContent class="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>{{ $t('settings.twoFactor.newCodes') }}</DialogTitle>
          <DialogDescription>{{ $t('settings.twoFactor.steps.codesHint') }}</DialogDescription>
        </DialogHeader>
        <RecoveryCodes :codes="renewedCodes" />
        <DialogFooter>
          <Button @click="codesOpen = false">{{ $t('settings.twoFactor.savedCodes') }}</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>

    <PasswordPromptDialog
      v-model:open="deleteOpen"
      :title="$t('settings.danger.deleteTitle')"
      :description="$t('settings.danger.deleteHint')"
      :confirm-label="$t('settings.danger.deleteConfirm')"
      destructive
      :action="deleteAccount"
    >
      <FormField id="delete-confirm" v-slot="aria" :label="$t('settings.danger.typeToConfirm', { word: deleteWord })">
        <Input id="delete-confirm" v-model="confirmText" v-bind="aria" autocomplete="off" />
      </FormField>
    </PasswordPromptDialog>
  </div>
</template>
