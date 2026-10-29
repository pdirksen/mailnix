<script setup lang="ts">
import AppLogo from '@/components/common/AppLogo.vue'
import AuthBackdrop from '@/components/common/AuthBackdrop.vue'
import LanguageSwitcher from '@/components/common/LanguageSwitcher.vue'
import ThemeToggle from '@/components/common/ThemeToggle.vue'
import { RouterView } from 'vue-router'
</script>

<template>
  <div class="relative isolate flex min-h-svh flex-col">
    <AuthBackdrop />
    <header class="flex items-center justify-between p-4">
      <AppLogo show-name />
      <div class="flex items-center gap-1">
        <LanguageSwitcher />
        <ThemeToggle />
      </div>
    </header>
    <main class="flex flex-1 items-start justify-center px-4 pt-[8vh] pb-12">
      <!-- The card floats above the backdrop. -->
      <div class="w-full max-w-sm [&_[data-slot=card]]:shadow-xl [&_[data-slot=card]]:shadow-black/5 dark:[&_[data-slot=card]]:shadow-black/40">
        <RouterView />
      </div>
    </main>
  </div>
</template>
