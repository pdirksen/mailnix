<script setup lang="ts">
import { Button } from '@/components/ui/button'
import { CheckIcon, CopyIcon } from '@lucide/vue'
import { ref } from 'vue'

/** Text to copy (an address, a token, a configuration snippet) with a copy button. */
const props = defineProps<{ value: string; label: string; multiline?: boolean }>()

const copied = ref(false)

async function copy() {
  try {
    await navigator.clipboard.writeText(props.value)
    copied.value = true
    setTimeout(() => (copied.value = false), 2000)
  } catch {
    copied.value = false
  }
}
</script>

<template>
  <div class="bg-muted/50 flex items-start gap-2 rounded-lg border p-2 pl-3">
    <pre
      v-if="multiline"
      class="min-w-0 flex-1 overflow-x-auto py-1 font-mono text-xs leading-relaxed"
      :aria-label="label"
    >{{ value }}</pre>
    <code v-else class="min-w-0 flex-1 py-1 font-mono text-sm break-all" :aria-label="label">{{ value }}</code>
    <Button type="button" variant="ghost" size="icon" class="size-7 shrink-0" :aria-label="label" @click="copy">
      <CheckIcon v-if="copied" class="text-emerald-600" />
      <CopyIcon v-else />
    </Button>
  </div>
</template>
