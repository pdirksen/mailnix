<script setup lang="ts">
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { useConnectionErrors } from '@/lib/connectionErrors'
import { useAccountsStore } from '@/stores/accounts'
import { useRunsStore } from '@/stores/runs'
import { Loader2Icon, PlayIcon } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { toast } from 'vue-sonner'

const props = defineProps<{
  accountId: string
  /** Only this rule (applied even if it is switched off); otherwise all active rules. */
  ruleId?: string
}>()
const open = defineModel<boolean>('open', { required: true })

const { t } = useI18n()
const router = useRouter()
const accounts = useAccountsStore()
const runs = useRunsStore()
const { describeRequestError } = useConnectionErrors()

const LIMITS = ['50', '100', '250', '500', '1000']
const folders = ref<string[]>([])
const folder = ref('')
const limit = ref('100')
const starting = ref(false)

const account = computed(() => accounts.accounts.find((a) => a.id === props.accountId))

watch(open, async (isOpen) => {
  if (!isOpen) return
  folder.value = account.value?.watchedFolders[0] ?? 'INBOX'
  folders.value = [folder.value]
  try {
    const list = await accounts.folders(props.accountId, false)
    folders.value = list.folders.filter((f) => f.canSelect).map((f) => f.path)
    if (!folders.value.includes(folder.value)) folders.value.unshift(folder.value)
  } catch {
    // The default folder stays selectable.
  }
})

async function start() {
  starting.value = true
  try {
    const run = await runs.start(props.accountId, {
      folder: folder.value,
      limit: Number(limit.value),
      ruleIds: props.ruleId ? [props.ruleId] : undefined,
    })
    open.value = false
    toast.success(t('runNow.started'), {
      action: { label: t('runNow.show'), onClick: () => void router.push({ name: 'activity', query: { run: run.id } }) },
    })
  } catch (error) {
    toast.error(describeRequestError(error).message)
  } finally {
    starting.value = false
  }
}
</script>

<template>
  <Dialog v-model:open="open">
    <DialogContent>
      <form class="grid gap-4" @submit.prevent="start">
        <DialogHeader>
          <DialogTitle>{{ ruleId ? $t('runNow.ruleTitle') : $t('runNow.title') }}</DialogTitle>
          <DialogDescription>{{ ruleId ? $t('runNow.ruleText') : $t('runNow.text') }}</DialogDescription>
        </DialogHeader>
        <div class="grid gap-2">
          <Label for="run-folder">{{ $t('runNow.folder') }}</Label>
          <Select v-model="folder">
            <SelectTrigger id="run-folder" class="w-full" data-testid="run-folder">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem v-for="path in folders" :key="path" :value="path">{{ path }}</SelectItem>
            </SelectContent>
          </Select>
        </div>
        <div class="grid gap-2">
          <Label for="run-limit">{{ $t('runNow.limit') }}</Label>
          <Select v-model="limit">
            <SelectTrigger id="run-limit" class="w-40">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem v-for="value in LIMITS" :key="value" :value="value">{{ value }}</SelectItem>
            </SelectContent>
          </Select>
        </div>
        <DialogFooter>
          <Button type="submit" :disabled="starting || !folder" data-testid="start-run">
            <Loader2Icon v-if="starting" class="animate-spin" />
            <PlayIcon v-else />
            {{ $t('runNow.start') }}
          </Button>
        </DialogFooter>
      </form>
    </DialogContent>
  </Dialog>
</template>
