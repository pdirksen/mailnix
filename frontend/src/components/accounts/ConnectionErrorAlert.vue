<script setup lang="ts">
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import type { DescribedError } from '@/lib/connectionErrors'
import { CircleAlertIcon } from '@lucide/vue'

defineProps<{ title: string; error: DescribedError }>()
</script>

<template>
  <Alert variant="destructive" data-testid="connection-error">
    <CircleAlertIcon />
    <AlertTitle>{{ title }}</AlertTitle>
    <AlertDescription>
      <p>{{ error.message }}</p>
      <p v-if="error.detail && error.detail !== error.message" class="mt-1 font-mono text-xs break-all opacity-80">
        {{ error.detail }}
      </p>
    </AlertDescription>
  </Alert>
</template>
