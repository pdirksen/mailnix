<script setup lang="ts">
import { ApiError, type Schemas } from '@/api/client'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { useConnectionErrors } from '@/lib/connectionErrors'
import { useProfileStore } from '@/stores/profile'
import { CircleAlertIcon, Loader2Icon } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import QrCode from './QrCode.vue'
import RecoveryCodes from './RecoveryCodes.vue'

/** Three steps: scan the QR code, confirm with a code, keep the recovery codes. */
const open = defineModel<boolean>('open', { required: true })

const { t } = useI18n()
const store = useProfileStore()
const { describeRequestError } = useConnectionErrors()

type Step = 'scan' | 'verify' | 'codes'
const step = ref<Step>('scan')
const setup = ref<Schemas['AuthenticatorSetup']>()
const code = ref('')
const codes = ref<string[]>([])
const error = ref<string>()
const busy = ref(false)
const showKey = ref(false)

const steps: Step[] = ['scan', 'verify', 'codes']
const stepIndex = computed(() => steps.indexOf(step.value))

watch(open, async (isOpen) => {
  if (!isOpen) return
  step.value = 'scan'
  setup.value = undefined
  code.value = ''
  codes.value = []
  error.value = undefined
  showKey.value = false
  try {
    setup.value = await store.startTwoFactorSetup()
  } catch (failure) {
    error.value = describeRequestError(failure).message
  }
})

async function verify() {
  const digits = code.value.replace(/\s/g, '')
  if (!/^\d{6}$/.test(digits)) {
    error.value = t('settings.twoFactor.codeFormat')
    return
  }
  busy.value = true
  error.value = undefined
  try {
    codes.value = await store.enableTwoFactor(digits)
    step.value = 'codes'
  } catch (failure) {
    error.value =
      failure instanceof ApiError && failure.code === 'InvalidCode'
        ? t('settings.twoFactor.invalidCode')
        : describeRequestError(failure).message
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <Dialog v-model:open="open">
    <DialogContent class="sm:max-w-lg" @interact-outside="(event) => step === 'codes' && event.preventDefault()">
      <DialogHeader>
        <DialogTitle>{{ $t('settings.twoFactor.setupTitle') }}</DialogTitle>
        <DialogDescription>{{ $t(`settings.twoFactor.steps.${step}Hint`) }}</DialogDescription>
      </DialogHeader>

      <ol class="flex items-center gap-2 text-xs" :aria-label="$t('settings.twoFactor.progress')">
        <li v-for="(item, index) in steps" :key="item" class="flex flex-1 items-center gap-2">
          <span
            class="flex size-6 shrink-0 items-center justify-center rounded-full border text-xs font-medium tabular-nums"
            :class="index <= stepIndex ? 'bg-primary text-primary-foreground border-primary' : 'text-muted-foreground'"
            :aria-current="index === stepIndex ? 'step' : undefined"
          >
            {{ index + 1 }}
          </span>
          <span :class="index === stepIndex ? 'font-medium' : 'text-muted-foreground'" class="truncate">
            {{ $t(`settings.twoFactor.steps.${item}`) }}
          </span>
          <span v-if="index < steps.length - 1" class="bg-border h-px flex-1" aria-hidden="true" />
        </li>
      </ol>

      <Alert v-if="error && step !== 'verify'" variant="destructive">
        <CircleAlertIcon />
        <AlertDescription>{{ error }}</AlertDescription>
      </Alert>

      <!-- 1. Scan -->
      <div v-if="step === 'scan'" class="grid justify-items-center gap-4">
        <Skeleton v-if="!setup && !error" class="size-48 rounded-lg" />
        <template v-else-if="setup">
          <div class="rounded-xl border p-3">
            <QrCode :value="setup.authenticatorUri" :label="$t('settings.twoFactor.qrLabel')" class="size-48" />
          </div>
          <button type="button" class="text-muted-foreground text-sm underline underline-offset-4" @click="showKey = !showKey">
            {{ $t('settings.twoFactor.cannotScan') }}
          </button>
          <div v-if="showKey" class="grid w-full gap-1 text-center">
            <span class="text-muted-foreground text-xs">{{ $t('settings.twoFactor.keyHint') }}</span>
            <code class="bg-muted rounded-md px-3 py-2 font-mono text-sm tracking-wider break-all select-all" data-testid="shared-key">
              {{ setup.sharedKey }}
            </code>
          </div>
        </template>
      </div>

      <!-- 2. Verify -->
      <form v-else-if="step === 'verify'" id="two-factor-verify" novalidate class="grid gap-2" @submit.prevent="verify">
        <Label for="two-factor-code">{{ $t('settings.twoFactor.code') }}</Label>
        <Input
          id="two-factor-code"
          v-model="code"
          inputmode="numeric"
          autocomplete="one-time-code"
          maxlength="7"
          placeholder="123 456"
          class="h-12 text-center font-mono text-xl tracking-[0.3em]"
          :aria-invalid="!!error"
          :aria-describedby="error ? 'two-factor-code-error' : undefined"
          autofocus
        />
        <p v-if="error" id="two-factor-code-error" class="text-destructive text-sm" role="alert">{{ error }}</p>
      </form>

      <!-- 3. Recovery codes -->
      <RecoveryCodes v-else :codes="codes" />

      <DialogFooter>
        <template v-if="step === 'scan'">
          <Button type="button" variant="outline" @click="open = false">{{ $t('common.cancel') }}</Button>
          <Button type="button" :disabled="!setup" @click="step = 'verify'">{{ $t('common.next') }}</Button>
        </template>
        <template v-else-if="step === 'verify'">
          <Button type="button" variant="outline" @click="step = 'scan'">{{ $t('common.back') }}</Button>
          <Button type="submit" form="two-factor-verify" :disabled="busy" data-testid="two-factor-verify">
            <Loader2Icon v-if="busy" class="animate-spin" />
            {{ $t('settings.twoFactor.turnOn') }}
          </Button>
        </template>
        <Button v-else type="button" data-testid="two-factor-done" @click="open = false">{{ $t('settings.twoFactor.savedCodes') }}</Button>
      </DialogFooter>
    </DialogContent>
  </Dialog>
</template>
