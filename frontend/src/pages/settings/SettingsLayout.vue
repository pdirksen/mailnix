<script setup lang="ts">
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { useConnectionErrors } from '@/lib/connectionErrors'
import { useProfileStore } from '@/stores/profile'
import { AtSignIcon, BotIcon, ShieldCheckIcon, SlidersHorizontalIcon, UserRoundIcon } from '@lucide/vue'
import { onMounted, ref } from 'vue'
import { RouterLink, RouterView } from 'vue-router'

const store = useProfileStore()
const { describeRequestError } = useConnectionErrors()
const failure = ref<string>()

const sections = [
  { name: 'settings-profile', key: 'profile', icon: UserRoundIcon },
  { name: 'settings-preferences', key: 'preferences', icon: SlidersHorizontalIcon },
  { name: 'settings-addresses', key: 'addresses', icon: AtSignIcon },
  { name: 'settings-security', key: 'security', icon: ShieldCheckIcon },
  { name: 'settings-assistants', key: 'assistants', icon: BotIcon },
] as const

async function load() {
  failure.value = undefined
  try {
    await store.load()
  } catch (error) {
    failure.value = describeRequestError(error).message
  }
}

onMounted(load)
</script>

<template>
  <div class="mx-auto grid max-w-5xl gap-6">
    <div>
      <h2 class="text-2xl font-semibold tracking-tight">{{ $t('settings.title') }}</h2>
      <p class="text-muted-foreground mt-1">{{ $t('settings.subtitle') }}</p>
    </div>

    <div class="grid gap-6 lg:grid-cols-[13rem_1fr] lg:gap-10">
      <nav :aria-label="$t('settings.sections')" class="-mx-4 overflow-x-auto px-4 lg:mx-0 lg:overflow-visible lg:px-0">
        <ul class="flex gap-1 lg:sticky lg:top-20 lg:flex-col" role="list">
          <li v-for="section in sections" :key="section.name" class="shrink-0">
            <RouterLink
              :to="{ name: section.name }"
              class="text-muted-foreground hover:bg-accent hover:text-accent-foreground focus-visible:ring-ring/50 flex items-center gap-2.5 rounded-md px-3 py-2 text-sm font-medium whitespace-nowrap outline-none focus-visible:ring-3"
              exact-active-class="!bg-accent !text-accent-foreground"
              :data-testid="`settings-nav-${section.key}`"
            >
              <component :is="section.icon" class="size-4" />
              {{ $t(`settings.${section.key}.nav`) }}
            </RouterLink>
          </li>
        </ul>
      </nav>

      <div class="min-w-0">
        <div v-if="failure" class="grid justify-items-start gap-3 rounded-xl border p-6">
          <p class="text-sm">{{ failure }}</p>
          <Button variant="outline" size="sm" @click="load">{{ $t('common.retry') }}</Button>
        </div>
        <div v-else-if="!store.profile" class="grid gap-6">
          <Skeleton class="h-40 rounded-xl" />
          <Skeleton class="h-56 rounded-xl" />
        </div>
        <RouterView v-else v-slot="{ Component }">
          <component :is="Component" :profile="store.profile" />
        </RouterView>
      </div>
    </div>
  </div>
</template>
