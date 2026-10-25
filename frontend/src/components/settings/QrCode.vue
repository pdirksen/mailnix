<script setup lang="ts">
import { encode } from 'uqr'
import { computed } from 'vue'

/** A QR code drawn as one SVG path; always dark on white, which every camera app reads. */
const props = defineProps<{ value: string; label: string }>()

const code = computed(() => encode(props.value, { ecc: 'M', border: 2 }))

const path = computed(() => {
  const commands: string[] = []
  code.value.data.forEach((row, y) =>
    row.forEach((dark, x) => {
      if (dark) commands.push(`M${x} ${y}h1v1h-1z`)
    }),
  )
  return commands.join('')
})
</script>

<template>
  <svg
    :viewBox="`0 0 ${code.size} ${code.size}`"
    role="img"
    :aria-label="label"
    shape-rendering="crispEdges"
    class="rounded-lg bg-white"
  >
    <path :d="path" fill="#000" />
  </svg>
</template>
