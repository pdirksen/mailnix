<script setup lang="ts">
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { useConnectionErrors } from '@/lib/connectionErrors'
import { useRunsStore } from '@/stores/runs'
import { Loader2Icon, Undo2Icon } from '@lucide/vue'
import { ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { toast } from 'vue-sonner'

const props = defineProps<{ ruleId: string; ruleName: string }>()
const open = defineModel<boolean>('open', { required: true })

const { t } = useI18n()
const router = useRouter()
const runs = useRunsStore()
const { describeRequestError } = useConnectionErrors()

const count = ref(10)
const starting = ref(false)
watch(open, (isOpen) => {
  if (isOpen) count.value = 10
})

async function start() {
  starting.value = true
  try {
    const run = await runs.undoRule(props.ruleId, Math.min(Math.max(Math.round(count.value) || 1, 1), 1000))
    open.value = false
    toast.success(t('activity.undoStarted'), {
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
          <DialogTitle>{{ $t('undoRule.title', { name: ruleName }) }}</DialogTitle>
          <DialogDescription>{{ $t('undoRule.text') }}</DialogDescription>
        </DialogHeader>
        <div class="grid gap-2">
          <Label for="undo-count">{{ $t('undoRule.count') }}</Label>
          <Input id="undo-count" v-model.number="count" type="number" min="1" max="1000" class="w-32" />
        </div>
        <DialogFooter>
          <Button type="submit" :disabled="starting" data-testid="start-undo-rule">
            <Loader2Icon v-if="starting" class="animate-spin" />
            <Undo2Icon v-else />
            {{ $t('undoRule.start') }}
          </Button>
        </DialogFooter>
      </form>
    </DialogContent>
  </Dialog>
</template>
