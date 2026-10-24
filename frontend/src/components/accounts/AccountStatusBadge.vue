<script setup lang="ts">
import { Badge } from '@/components/ui/badge'
import type { Account } from '@/stores/accounts'
import { CircleAlertIcon, CircleCheckIcon, CircleDashedIcon, PauseIcon } from '@lucide/vue'
import { computed } from 'vue'

const props = defineProps<{ status: Account['status']; enabled?: boolean }>()

const icon = computed(() => ({ Ok: CircleCheckIcon, Error: CircleAlertIcon, Unknown: CircleDashedIcon })[props.status])
</script>

<template>
  <span class="inline-flex items-center gap-1.5">
    <Badge
      variant="outline"
      :class="{
        'border-success/40 text-success': status === 'Ok',
        'border-destructive/40 text-destructive': status === 'Error',
      }"
      data-testid="account-status"
    >
      <component :is="icon" class="size-3" />
      {{ $t(`accounts.status.${status}`) }}
    </Badge>
    <Badge v-if="enabled === false" variant="secondary">
      <PauseIcon class="size-3" />
      {{ $t('accounts.disabled') }}
    </Badge>
  </span>
</template>
