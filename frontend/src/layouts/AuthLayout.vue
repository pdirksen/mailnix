<script setup lang="ts">
import AppLogo from '@/components/common/AppLogo.vue'
import LanguageSwitcher from '@/components/common/LanguageSwitcher.vue'
import ThemeToggle from '@/components/common/ThemeToggle.vue'
import { RouterView } from 'vue-router'
</script>

<template>
  <div class="bg-muted/40 flex min-h-svh flex-col">
    <header class="flex items-center justify-between p-4">
      <AppLogo show-name />
      <div class="flex items-center gap-1">
        <LanguageSwitcher />
        <ThemeToggle />
      </div>
    </header>
    <main class="flex flex-1 items-start justify-center px-4 pt-[8vh] pb-12">
      <div class="w-full max-w-sm">
        <RouterView />
        <p class="text-muted-foreground mt-6 text-center text-xs">{{ $t('app.tagline') }}</p>
      </div>
    </main>
  </div>
</template>
