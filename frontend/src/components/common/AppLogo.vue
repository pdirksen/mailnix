<script setup lang="ts">
import logoMark from '@/assets/brand/logo-mark.svg'

defineProps<{ showName?: boolean }>()
</script>

<template>
  <span class="flex items-center gap-2 font-semibold">
    <!-- The mark keeps its own colors in both themes. On dark surfaces a faint ring, drawn inside the tile (so a
         clipping parent such as the collapsed sidebar cannot cut it off), sets the black tile apart. -->
    <span class="relative size-8 shrink-0">
      <img :src="logoMark" alt="" width="32" height="32" class="size-8" />
      <span class="pointer-events-none absolute inset-0 rounded-[22%] dark:ring-1 dark:ring-white/15 dark:ring-inset" aria-hidden="true" />
    </span>
    <span v-if="showName" class="truncate text-base">{{ $t('app.name') }}</span>
  </span>
</template>
