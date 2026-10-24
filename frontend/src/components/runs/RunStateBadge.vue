<script setup lang="ts">
import { Badge } from '@/components/ui/badge'
import type { RunState } from '@/stores/runs'
import { Loader2Icon } from '@lucide/vue'
import { computed } from 'vue'

const props = defineProps<{ state: RunState; undone?: boolean }>()

const style = computed(() => {
  if (props.undone) return 'border-muted-foreground/40 text-muted-foreground'
  switch (props.state) {
    case 'Completed':
      return 'border-success/40 text-success'
    case 'AwaitingConfirmation':
    case 'CompletedWithErrors':
      return 'border-warning/50 text-warning'
    case 'Failed':
      return 'border-destructive/50 text-destructive'
    default:
      return 'text-muted-foreground'
  }
})
</script>

<template>
  <Badge variant="outline" :class="style" :data-state="state">
    <Loader2Icon v-if="state === 'Running' || state === 'Queued'" class="animate-spin" />
    {{ undone ? $t('activity.undone') : $t(`activity.states.${state}`) }}
  </Badge>
</template>
