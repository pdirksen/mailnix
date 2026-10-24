<script setup lang="ts">
import { Toaster } from '@/components/ui/sonner'
import { useTitle } from '@vueuse/core'
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterView, useRoute } from 'vue-router'

const route = useRoute()
const { t } = useI18n()

useTitle(computed(() => (route.meta.titleKey ? `${t(route.meta.titleKey)} · mailnix` : 'mailnix')))
</script>

<template>
  <RouterView />
  <Toaster rich-colors close-button />
</template>
