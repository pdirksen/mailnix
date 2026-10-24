<script setup lang="ts">
import { CheckIcon, MinusIcon } from '@lucide/vue'

/** Capabilities that change how mailnix works with the server, with a short explanation each. */
const IMPORTANT = ['Idle', 'Move', 'UidPlus', 'SpecialUse', 'CondStore'] as const

defineProps<{ capabilities: readonly string[] }>()
</script>

<template>
  <ul class="grid gap-2 text-sm sm:grid-cols-2" data-testid="capabilities">
    <li v-for="name in IMPORTANT" :key="name" class="flex items-center gap-2">
      <CheckIcon v-if="capabilities.includes(name)" class="text-success size-4 shrink-0" :aria-label="'✓'" />
      <MinusIcon v-else class="text-muted-foreground size-4 shrink-0" :aria-label="'–'" />
      <span :class="{ 'text-muted-foreground': !capabilities.includes(name) }">
        {{ $t(`accounts.overview.capabilityHints.${name}`) }}
      </span>
    </li>
  </ul>
</template>
