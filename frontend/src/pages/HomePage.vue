<script setup lang="ts">
import { Card, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { useAuthStore } from '@/stores/auth'

// Becomes the dashboard in milestone 8.
const auth = useAuthStore()
</script>

<template>
  <div class="mx-auto grid max-w-3xl gap-6">
    <div>
      <h2 class="text-2xl font-semibold tracking-tight">{{ $t('home.welcome', { name: auth.displayName }) }}</h2>
      <p class="text-muted-foreground mt-2">{{ $t('home.intro') }}</p>
    </div>
    <Card>
      <CardHeader>
        <CardTitle>{{ $t('home.nextStepsTitle') }}</CardTitle>
        <CardDescription>
          {{ $t('home.nextSteps', { role: auth.isAdmin ? $t('home.roleAdmin') : $t('home.roleUser') }) }}
        </CardDescription>
      </CardHeader>
    </Card>
  </div>
</template>
