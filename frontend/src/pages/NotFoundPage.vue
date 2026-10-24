<script setup lang="ts">
import AppLogo from '@/components/common/AppLogo.vue'
import { Button } from '@/components/ui/button'
import { RouterLink } from 'vue-router'
</script>

<template>
  <div class="flex min-h-svh flex-col items-center justify-center gap-4 p-4 text-center">
    <AppLogo />
    <h1 class="text-2xl font-semibold">{{ $t('notFound.title') }}</h1>
    <p class="text-muted-foreground">{{ $t('notFound.description') }}</p>
    <Button as-child variant="outline">
      <RouterLink :to="{ name: 'dashboard' }">{{ $t('notFound.back') }}</RouterLink>
    </Button>
  </div>
</template>
