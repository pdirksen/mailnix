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
import { isLocale, LOCALE_NAMES, SUPPORTED_LOCALES } from '@/i18n'
import { useAuthStore } from '@/stores/auth'
import { useProfileStore } from '@/stores/profile'
import { useUiStore } from '@/stores/ui'
import { LanguagesIcon } from '@lucide/vue'

const ui = useUiStore()
const auth = useAuthStore()

function onSelect(value: unknown) {
  if (!isLocale(value)) return
  ui.setLocale(value)
  // Signed in: the choice follows the user to other browsers (best effort; the switch itself already happened).
  if (auth.isAuthenticated) void useProfileStore().update({ language: value }).catch(() => {})
}
</script>

<template>
  <DropdownMenu>
    <DropdownMenuTrigger as-child>
      <Button variant="ghost" size="icon" :aria-label="$t('common.language')">
        <LanguagesIcon class="size-4" />
      </Button>
    </DropdownMenuTrigger>
    <DropdownMenuContent align="end">
      <DropdownMenuLabel>{{ $t('common.language') }}</DropdownMenuLabel>
      <DropdownMenuSeparator />
      <DropdownMenuRadioGroup :model-value="ui.locale" @update:model-value="onSelect">
        <DropdownMenuRadioItem v-for="locale in SUPPORTED_LOCALES" :key="locale" :value="locale">
          {{ LOCALE_NAMES[locale] }}
        </DropdownMenuRadioItem>
      </DropdownMenuRadioGroup>
    </DropdownMenuContent>
  </DropdownMenu>
</template>
