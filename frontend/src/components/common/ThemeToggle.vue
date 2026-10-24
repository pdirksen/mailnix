<script setup lang="ts">
import { Button } from '@/components/ui/button'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuLabel,
  DropdownMenuRadioGroup,
  DropdownMenuRadioItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { type ThemePreference, useUiStore } from '@/stores/ui'
import { MonitorIcon, MoonIcon, SunIcon } from '@lucide/vue'

const ui = useUiStore()

const options: { value: ThemePreference; labelKey: string; icon: typeof SunIcon }[] = [
  { value: 'light', labelKey: 'common.themeLight', icon: SunIcon },
  { value: 'dark', labelKey: 'common.themeDark', icon: MoonIcon },
  { value: 'auto', labelKey: 'common.themeSystem', icon: MonitorIcon },
]
</script>

<template>
  <DropdownMenu>
    <DropdownMenuTrigger as-child>
      <Button variant="ghost" size="icon" :aria-label="$t('common.theme')">
        <SunIcon class="size-4 dark:hidden" />
        <MoonIcon class="hidden size-4 dark:block" />
      </Button>
    </DropdownMenuTrigger>
    <DropdownMenuContent align="end">
      <DropdownMenuLabel>{{ $t('common.theme') }}</DropdownMenuLabel>
      <DropdownMenuSeparator />
      <DropdownMenuRadioGroup
        :model-value="ui.theme"
        @update:model-value="(value) => ui.setTheme(value as ThemePreference)"
      >
        <DropdownMenuRadioItem v-for="option in options" :key="option.value" :value="option.value">
          <component :is="option.icon" class="size-4" />
          {{ $t(option.labelKey) }}
        </DropdownMenuRadioItem>
      </DropdownMenuRadioGroup>
    </DropdownMenuContent>
  </DropdownMenu>
</template>
