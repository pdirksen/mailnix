<script setup lang="ts">
import { ApiError } from '@/api/client'
import FormField from '@/components/common/FormField.vue'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { useConnectionErrors } from '@/lib/connectionErrors'
import { Loader2Icon } from '@lucide/vue'
import { ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'

/** Asks for the current password before a sensitive change; `action` does the change and may fail. */
const props = defineProps<{
  title: string
  description: string
  confirmLabel: string
  destructive?: boolean
  action: (password: string) => Promise<void>
}>()
const open = defineModel<boolean>('open', { required: true })

const { t, te } = useI18n()
const { describeRequestError } = useConnectionErrors()
const password = ref('')
const error = ref<string>()
const busy = ref(false)

watch(open, (isOpen) => {
  if (isOpen) {
    password.value = ''
    error.value = undefined
  }
})

async function confirm() {
  if (!password.value) {
    error.value = t('validation.required')
    return
  }
  busy.value = true
  error.value = undefined
  try {
    await props.action(password.value)
    open.value = false
  } catch (failure) {
    if (failure instanceof ApiError) {
      const key = `settings.errors.${failure.code}`
      error.value = te(key) ? t(key) : describeRequestError(failure).message
    } else {
      // A check of the caller, e.g. "type DELETE to confirm".
      error.value = failure instanceof Error ? failure.message : t('common.unexpectedError')
    }
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <Dialog v-model:open="open">
    <DialogContent class="sm:max-w-md">
      <form novalidate class="grid gap-4" @submit.prevent="confirm">
        <DialogHeader>
          <DialogTitle>{{ title }}</DialogTitle>
          <DialogDescription>{{ description }}</DialogDescription>
        </DialogHeader>
        <slot />
        <FormField id="confirm-password" v-slot="aria" :label="$t('settings.security.currentPassword')" :error="error">
          <Input id="confirm-password" v-model="password" v-bind="aria" type="password" autocomplete="current-password" />
        </FormField>
        <DialogFooter>
          <Button type="button" variant="outline" @click="open = false">{{ $t('common.cancel') }}</Button>
          <Button type="submit" :variant="destructive ? 'destructive' : 'default'" :disabled="busy" data-testid="confirm-password-submit">
            <Loader2Icon v-if="busy" class="animate-spin" />
            {{ confirmLabel }}
          </Button>
        </DialogFooter>
      </form>
    </DialogContent>
  </Dialog>
</template>
