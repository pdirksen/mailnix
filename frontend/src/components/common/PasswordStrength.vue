<script setup lang="ts">
import { passwordStrength } from '@/lib/passwordStrength'
import { computed } from 'vue'

/** A three-step bar with a hint, shown under a new-password field. */
const props = defineProps<{ password: string }>()
const strength = computed(() => passwordStrength(props.password))
</script>

<template>
  <div v-if="password" class="-mt-2 grid gap-1" data-testid="password-strength">
    <div class="flex gap-1" aria-hidden="true">
      <span
        v-for="level in 3"
        :key="level"
        class="h-1 flex-1 rounded-full transition-colors"
        :class="level <= strength.level ? ['bg-destructive', 'bg-amber-500', 'bg-emerald-600'][strength.level - 1] : 'bg-muted'"
      />
    </div>
    <span class="text-muted-foreground text-xs">{{ $t(`settings.security.strength.${strength.label}`) }}</span>
  </div>
</template>
