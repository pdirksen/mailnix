<script setup lang="ts">
import { MailCheckIcon } from '@lucide/vue'

defineProps<{ showName?: boolean }>()
</script>

<template>
  <span class="flex items-center gap-2 font-semibold">
    <span class="bg-primary text-primary-foreground flex size-8 shrink-0 items-center justify-center rounded-lg">
      <MailCheckIcon class="size-4" />
    </span>
    <span v-if="showName" class="truncate text-base">{{ $t('app.name') }}</span>
  </span>
</template>
