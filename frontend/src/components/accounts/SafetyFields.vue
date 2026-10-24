<script setup lang="ts">
import FormField from '@/components/common/FormField.vue'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Switch } from '@/components/ui/switch'
import { formatRelative } from '@/lib/format'
import type { SafetySettings } from './safety'

defineProps<{ errors: Record<string, string>; lastPurgeAt?: string | null }>()
const safety = defineModel<SafetySettings>({ required: true })

const DEFAULT_PURGE_DAYS = 30

function togglePurge(enabled: boolean) {
  safety.value.purgeTrashAfterDays = enabled ? DEFAULT_PURGE_DAYS : null
}
</script>

<template>
  <div class="grid gap-5">
    <FormField
      id="blast-radius"
      v-slot="aria"
      :label="$t('accounts.safety.blastRadius')"
      :hint="$t('accounts.safety.blastRadiusHint')"
      :error="errors.blastRadiusLimit"
    >
      <Input id="blast-radius" v-model.number="safety.blastRadiusLimit" v-bind="aria" type="number" min="1" max="100000" class="w-32" />
    </FormField>

    <div class="grid gap-3">
      <div class="flex items-start gap-3">
        <Switch
          id="purge-trash"
          :model-value="safety.purgeTrashAfterDays !== null"
          class="mt-0.5"
          @update:model-value="togglePurge"
        />
        <Label for="purge-trash" class="grid gap-1 font-normal">
          <span class="font-medium">{{ $t('accounts.safety.purgeTrash') }}</span>
          <span class="text-muted-foreground text-xs">{{ $t('accounts.safety.purgeTrashHint') }}</span>
        </Label>
      </div>
      <FormField
        v-if="safety.purgeTrashAfterDays !== null"
        id="purge-days"
        v-slot="aria"
        :label="$t('accounts.safety.purgeDays')"
        :error="errors.purgeTrashAfterDays"
        class="ml-12"
      >
        <Input id="purge-days" v-model.number="safety.purgeTrashAfterDays" v-bind="aria" type="number" min="1" max="3650" class="w-32" />
        <p v-if="lastPurgeAt" class="text-muted-foreground text-xs">
          {{ $t('accounts.safety.lastPurge', { time: formatRelative(lastPurgeAt, $i18n.locale) }) }}
        </p>
      </FormField>
    </div>
  </div>
</template>
