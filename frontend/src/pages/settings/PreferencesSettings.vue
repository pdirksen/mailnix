<script setup lang="ts">
import TimeZonePicker from '@/components/settings/TimeZonePicker.vue'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Label } from '@/components/ui/label'
import { type Locale, LOCALE_NAMES, SUPPORTED_LOCALES } from '@/i18n'
import { useConnectionErrors } from '@/lib/connectionErrors'
import { browserTimeZone, describeTimeZone, timeIn } from '@/lib/timeZones'
import { type Profile, useProfileStore } from '@/stores/profile'
import { type ThemePreference, useUiStore } from '@/stores/ui'
import { CheckIcon, ClockIcon, FolderIcon, MonitorIcon, MoonIcon, SunIcon } from '@lucide/vue'
import { useIntervalFn } from '@vueuse/core'
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { toast } from 'vue-sonner'

const props = defineProps<{ profile: Profile }>()

const { t, locale } = useI18n()
const ui = useUiStore()
const store = useProfileStore()
const { describeRequestError } = useConnectionErrors()
const now = ref(new Date())
useIntervalFn(() => (now.value = new Date()), 30_000)

async function save(changes: Parameters<typeof store.update>[0], message: string) {
  try {
    await store.update(changes)
    toast.success(message)
  } catch (error) {
    toast.error(describeRequestError(error).message)
  }
}

// Language: applies at once, and follows the user to other browsers.
async function chooseLanguage(language: Locale) {
  if (language === ui.locale && props.profile.language === language) return
  ui.setLocale(language)
  await save({ language }, t('settings.saved'))
}

// Theme: a choice of this browser only.
const themes: { value: ThemePreference; icon: typeof SunIcon }[] = [
  { value: 'light', icon: SunIcon },
  { value: 'dark', icon: MoonIcon },
  { value: 'auto', icon: MonitorIcon },
]

// Time zone
const timeZone = ref(props.profile.timeZone)
watch(
  () => props.profile.timeZone,
  (zone) => (timeZone.value = zone),
)
watch(timeZone, async (zone) => {
  if (zone && zone !== props.profile.timeZone) await save({ timeZone: zone }, t('settings.timeZone.saved', { zone }))
})
const browserZone = browserTimeZone()
const suggestBrowserZone = computed(() => !!browserZone && browserZone !== props.profile.timeZone)
const zoneInfo = computed(() => describeTimeZone(timeZone.value, now.value))
const localTime = computed(() => timeIn(timeZone.value, locale.value, now.value))
/** What "Archive/{year}/{month}" becomes for a message arriving now. */
const exampleFolder = computed(() => {
  const parts = new Intl.DateTimeFormat('en-CA', { timeZone: timeZone.value, year: 'numeric', month: '2-digit' }).formatToParts(now.value)
  const part = (type: string) => parts.find((p) => p.type === type)?.value ?? ''
  return `Archive/${part('year')}/${part('month')}`
})
</script>

<template>
  <div class="grid gap-6">
    <Card>
      <CardHeader>
        <CardTitle>{{ $t('settings.preferences.language') }}</CardTitle>
        <CardDescription>{{ $t('settings.preferences.languageHint') }}</CardDescription>
      </CardHeader>
      <CardContent>
        <div role="radiogroup" :aria-label="$t('settings.preferences.language')" class="grid gap-3 sm:grid-cols-2">
          <button
            v-for="language in SUPPORTED_LOCALES"
            :key="language"
            type="button"
            role="radio"
            :aria-checked="ui.locale === language"
            class="hover:bg-accent/50 focus-visible:ring-ring/50 flex items-center gap-3 rounded-lg border p-3 text-left transition-colors outline-none focus-visible:ring-3"
            :class="ui.locale === language ? 'border-primary ring-primary/20 ring-2' : ''"
            :data-testid="`language-${language}`"
            @click="chooseLanguage(language)"
          >
            <span class="bg-muted flex size-9 items-center justify-center rounded-md text-xs font-semibold uppercase">{{ language }}</span>
            <span class="flex-1 font-medium">{{ LOCALE_NAMES[language] }}</span>
            <CheckIcon v-if="ui.locale === language" class="text-primary size-4" />
          </button>
        </div>
      </CardContent>
    </Card>

    <Card>
      <CardHeader>
        <CardTitle>{{ $t('common.theme') }}</CardTitle>
        <CardDescription>{{ $t('settings.preferences.themeHint') }}</CardDescription>
      </CardHeader>
      <CardContent>
        <div role="radiogroup" :aria-label="$t('common.theme')" class="grid grid-cols-3 gap-3">
          <button
            v-for="theme in themes"
            :key="theme.value"
            type="button"
            role="radio"
            :aria-checked="ui.theme === theme.value"
            class="focus-visible:ring-ring/50 group grid gap-2 rounded-lg text-left outline-none focus-visible:ring-3"
            :data-testid="`theme-${theme.value}`"
            @click="ui.setTheme(theme.value)"
          >
            <span
              class="relative block aspect-[4/3] overflow-hidden rounded-lg border-2 transition-colors"
              :class="ui.theme === theme.value ? 'border-primary' : 'border-border group-hover:border-muted-foreground/40'"
              aria-hidden="true"
            >
              <!-- A tiny picture of the app in that theme; "system" shows both halves. -->
              <span v-if="theme.value !== 'dark'" class="absolute inset-0 flex bg-white" :class="theme.value === 'auto' ? 'w-1/2' : ''">
                <span class="w-1/4 border-r border-zinc-200 bg-zinc-50" />
                <span class="grid flex-1 content-start gap-1.5 p-2">
                  <span class="h-1.5 w-2/3 rounded bg-zinc-300" />
                  <span class="h-1.5 w-1/2 rounded bg-zinc-200" />
                  <span class="mt-1 h-5 rounded bg-zinc-100" />
                </span>
              </span>
              <span
                v-if="theme.value !== 'light'"
                class="absolute inset-y-0 right-0 flex bg-zinc-950"
                :class="theme.value === 'auto' ? 'w-1/2' : 'left-0'"
              >
                <span v-if="theme.value === 'dark'" class="w-1/4 border-r border-zinc-800 bg-zinc-900" />
                <span class="grid flex-1 content-start gap-1.5 p-2">
                  <span class="h-1.5 w-2/3 rounded bg-zinc-600" />
                  <span class="h-1.5 w-1/2 rounded bg-zinc-700" />
                  <span class="mt-1 h-5 rounded bg-zinc-800" />
                </span>
              </span>
            </span>
            <span class="flex items-center gap-1.5 text-sm font-medium">
              <component :is="theme.icon" class="size-4" />
              {{ $t(theme.value === 'auto' ? 'common.themeSystem' : theme.value === 'light' ? 'common.themeLight' : 'common.themeDark') }}
            </span>
          </button>
        </div>
      </CardContent>
    </Card>

    <Card>
      <CardHeader>
        <CardTitle>{{ $t('settings.timeZone.title') }}</CardTitle>
        <CardDescription>{{ $t('settings.timeZone.description') }}</CardDescription>
      </CardHeader>
      <CardContent class="grid gap-4">
        <div
          v-if="suggestBrowserZone"
          class="bg-muted/50 flex flex-wrap items-center gap-3 rounded-lg border px-3 py-2 text-sm"
          data-testid="browser-zone-hint"
        >
          <ClockIcon class="text-muted-foreground size-4 shrink-0" />
          <span class="flex-1">{{ $t('settings.timeZone.browserDiffers', { zone: browserZone }) }}</span>
          <Button size="sm" variant="outline" @click="timeZone = browserZone!">{{ $t('settings.timeZone.useBrowser') }}</Button>
        </div>
        <div class="grid gap-2">
          <Label for="time-zone">{{ $t('settings.timeZone.title') }}</Label>
          <TimeZonePicker id="time-zone" v-model="timeZone" />
        </div>
        <dl class="grid gap-3 text-sm sm:grid-cols-2">
          <div class="flex items-center gap-3 rounded-lg border px-3 py-2.5">
            <ClockIcon class="text-muted-foreground size-4 shrink-0" />
            <div>
              <dt class="text-muted-foreground text-xs">{{ $t('settings.timeZone.localTime') }}</dt>
              <dd class="font-medium tabular-nums" data-testid="zone-time">{{ localTime }} · {{ zoneInfo.offset }}</dd>
            </div>
          </div>
          <div class="flex items-center gap-3 rounded-lg border px-3 py-2.5">
            <FolderIcon class="text-muted-foreground size-4 shrink-0" />
            <div class="min-w-0">
              <dt class="text-muted-foreground text-xs">{{ $t('settings.timeZone.exampleFolder') }}</dt>
              <dd class="truncate font-mono text-xs font-medium">{{ exampleFolder }}</dd>
            </div>
          </div>
        </dl>
      </CardContent>
    </Card>
  </div>
</template>
