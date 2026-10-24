<script setup lang="ts">
import { Input } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { computed } from 'vue'

defineProps<{ inputId: string; invalid?: boolean }>()
const schedule = defineModel<string>({ required: true })

const PRESETS = { hourly: '0 * * * *', daily: '0 3 * * *', weekly: '0 3 * * 1' } as const
type Preset = keyof typeof PRESETS | 'custom'

const preset = computed<Preset>({
  get: () => (Object.entries(PRESETS).find(([, cron]) => cron === schedule.value.trim())?.[0] as Preset | undefined) ?? 'custom',
  set: (value) => {
    if (value !== 'custom') schedule.value = PRESETS[value]
  },
})
</script>

<template>
  <div class="flex flex-wrap gap-2">
    <Select v-model="preset">
      <SelectTrigger :id="inputId" class="w-56">
        <SelectValue />
      </SelectTrigger>
      <SelectContent>
        <SelectItem v-for="name in ['hourly', 'daily', 'weekly', 'custom']" :key="name" :value="name">
          {{ $t(`rules.editor.schedulePresets.${name}`) }}
        </SelectItem>
      </SelectContent>
    </Select>
    <Input v-model="schedule" class="w-40 font-mono" placeholder="0 3 * * *" spellcheck="false" :aria-label="$t('rules.editor.schedule')" :aria-invalid="invalid" />
  </div>
</template>
