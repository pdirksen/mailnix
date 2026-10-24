<script setup lang="ts">
import { Button } from '@/components/ui/button'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { formatRelative } from '@/lib/format'
import { useRunsStore } from '@/stores/runs'
import { BellIcon } from '@lucide/vue'
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterLink } from 'vue-router'

const runs = useRunsStore()
const { locale } = useI18n()
const open = ref(false)
</script>

<template>
  <Popover v-model:open="open">
    <PopoverTrigger as-child>
      <Button variant="ghost" size="icon" class="relative" :aria-label="$t('activity.pendingBell')" :title="$t('activity.pendingBell')" data-testid="pending-bell">
        <BellIcon />
        <span
          v-if="runs.pending.length"
          class="bg-warning text-background absolute top-1 right-1 flex min-w-4 items-center justify-center rounded-full px-1 text-[10px] leading-4 font-semibold"
          data-testid="pending-count"
        >
          {{ runs.pending.length }}
        </span>
      </Button>
    </PopoverTrigger>
    <PopoverContent align="end" class="w-80 p-0">
      <p class="border-b px-4 py-3 text-sm font-medium">{{ $t('activity.pendingBell') }}</p>
      <p v-if="runs.pending.length === 0" class="text-muted-foreground px-4 py-6 text-center text-sm">—</p>
      <ul v-else class="max-h-80 overflow-y-auto py-1">
        <li v-for="run in runs.pending" :key="run.id">
          <RouterLink
            :to="{ name: 'run-review', params: { id: run.id } }"
            class="hover:bg-accent grid gap-0.5 px-4 py-2 text-sm"
            @click="open = false"
          >
            <span class="font-medium">{{ run.accountName }} · {{ run.folder }}</span>
            <span class="text-muted-foreground text-xs">
              {{ $t('activity.messagesAffected', { count: run.messagesMatched }) }} · {{ formatRelative(run.createdAt, locale) }}
            </span>
          </RouterLink>
        </li>
      </ul>
    </PopoverContent>
  </Popover>
</template>
