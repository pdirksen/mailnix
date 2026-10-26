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
import { BASE_THEME_OPTIONS, COLOR_THEME_OPTIONS, type ThemePreference } from '@/lib/themes'
import { useUiStore } from '@/stores/ui'
import { MoonIcon, SunIcon } from '@lucide/vue'

const ui = useUiStore()

const groups = [
  { labelKey: 'common.theme', options: BASE_THEME_OPTIONS },
  { labelKey: 'common.colorThemes', options: COLOR_THEME_OPTIONS },
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
      <template v-for="(group, index) in groups" :key="group.labelKey">
        <DropdownMenuSeparator v-if="index > 0" />
        <DropdownMenuLabel>{{ $t(group.labelKey) }}</DropdownMenuLabel>
        <DropdownMenuRadioGroup
          :model-value="ui.theme"
          @update:model-value="(value) => ui.setTheme(value as ThemePreference)"
        >
          <DropdownMenuRadioItem v-for="option in group.options" :key="option.value" :value="option.value">
            <component :is="option.icon" class="size-4" />
            {{ $t(option.labelKey) }}
          </DropdownMenuRadioItem>
        </DropdownMenuRadioGroup>
      </template>
    </DropdownMenuContent>
  </DropdownMenu>
</template>
