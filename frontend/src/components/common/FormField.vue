<script setup lang="ts">
import { Label } from '@/components/ui/label'

/** Label + control + hint/error, wired up with the ids screen readers need. */
defineProps<{
  id: string
  label: string
  error?: string
  hint?: string
}>()
</script>

<template>
  <div class="grid gap-2">
    <Label :for="id">{{ label }}</Label>
    <slot :aria-invalid="!!error" :aria-describedby="error ? `${id}-error` : hint ? `${id}-hint` : undefined" />
    <p v-if="error" :id="`${id}-error`" class="text-destructive text-sm" role="alert">{{ error }}</p>
    <p v-else-if="hint" :id="`${id}-hint`" class="text-muted-foreground text-xs">{{ hint }}</p>
  </div>
</template>
