<script setup lang="ts">
import { allTimeZones, describeTimeZone, matchesTimeZone, timeIn, type TimeZoneOption } from '@/lib/timeZones'
import { CheckIcon, ChevronsUpDownIcon, SearchIcon } from '@lucide/vue'
import {
  ComboboxAnchor,
  ComboboxContent,
  ComboboxEmpty,
  ComboboxInput,
  ComboboxItem,
  ComboboxItemIndicator,
  ComboboxPortal,
  ComboboxRoot,
  ComboboxTrigger,
  ComboboxViewport,
} from 'reka-ui'
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'

/** Searchable list of all time zones with their offset and the current time there. */
const props = defineProps<{ id: string; disabled?: boolean }>()
const model = defineModel<string>({ required: true })

const { t, locale } = useI18n()
const query = ref('')

// Offsets change with daylight saving time; computed once per page view is close enough.
const options = allTimeZones()
  .map((zone) => describeTimeZone(zone))
  .sort((a, b) => a.offsetMinutes - b.offsetMinutes || a.id.localeCompare(b.id))
const byId = new Map(options.map((option) => [option.id, option]))

/** At most this many rows are rendered; typing narrows the list. */
const LIMIT = 80
const filtered = computed(() => options.filter((option) => matchesTimeZone(option, query.value)))
const shown = computed(() => filtered.value.slice(0, LIMIT))

function label(zone: string | undefined) {
  const option = zone ? byId.get(zone) : undefined
  return option ? `${option.city}${option.region ? ` (${option.region})` : ''}` : (zone ?? '')
}

function detail(option: TimeZoneOption) {
  return `${option.offset} · ${timeIn(option.id, locale.value)}`
}
</script>

<template>
  <ComboboxRoot
    v-model="model"
    ignore-filter
    open-on-click
    :disabled="props.disabled"
    @update:open="(open) => open || (query = '')"
  >
    <ComboboxAnchor
      class="border-input focus-within:border-ring focus-within:ring-ring/50 dark:bg-input/30 flex h-9 w-full items-center gap-2 rounded-md border bg-transparent px-3 shadow-xs transition-[color,box-shadow] focus-within:ring-[3px]"
    >
      <SearchIcon class="text-muted-foreground size-4 shrink-0" aria-hidden="true" />
      <ComboboxInput
        :id="props.id"
        v-model="query"
        :display-value="label"
        :placeholder="t('settings.timeZone.search')"
        class="placeholder:text-muted-foreground h-full min-w-0 flex-1 bg-transparent text-sm outline-none"
        data-testid="time-zone-input"
      />
      <ComboboxTrigger :aria-label="t('settings.timeZone.showAll')" class="text-muted-foreground">
        <ChevronsUpDownIcon class="size-4" />
      </ComboboxTrigger>
    </ComboboxAnchor>

    <ComboboxPortal>
      <ComboboxContent
        position="popper"
        :side-offset="4"
        class="bg-popover text-popover-foreground data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0 z-50 w-(--reka-combobox-trigger-width) min-w-72 overflow-hidden rounded-md border shadow-md"
      >
        <ComboboxViewport class="max-h-72 overflow-y-auto p-1">
          <ComboboxEmpty class="text-muted-foreground px-2 py-6 text-center text-sm">
            {{ t('settings.timeZone.noMatch') }}
          </ComboboxEmpty>
          <ComboboxItem
            v-for="option in shown"
            :key="option.id"
            :value="option.id"
            class="data-[highlighted]:bg-accent data-[highlighted]:text-accent-foreground relative flex cursor-default items-center gap-2 rounded-sm py-1.5 pr-2 pl-8 text-sm outline-none select-none"
          >
            <ComboboxItemIndicator class="absolute left-2 inline-flex size-4 items-center justify-center">
              <CheckIcon class="size-4" />
            </ComboboxItemIndicator>
            <span class="min-w-0 flex-1 truncate">
              {{ option.city }}
              <span v-if="option.region" class="text-muted-foreground">· {{ option.region }}</span>
            </span>
            <span class="text-muted-foreground shrink-0 text-xs tabular-nums">{{ detail(option) }}</span>
          </ComboboxItem>
          <p v-if="filtered.length > LIMIT" class="text-muted-foreground px-2 py-2 text-xs">
            {{ t('settings.timeZone.more', { count: filtered.length - LIMIT }) }}
          </p>
        </ComboboxViewport>
      </ComboboxContent>
    </ComboboxPortal>
  </ComboboxRoot>
</template>
