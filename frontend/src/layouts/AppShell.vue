<script setup lang="ts">
import AppLogo from '@/components/common/AppLogo.vue'
import LanguageSwitcher from '@/components/common/LanguageSwitcher.vue'
import ThemeToggle from '@/components/common/ThemeToggle.vue'
import UserMenu from '@/components/common/UserMenu.vue'
import PendingBell from '@/components/runs/PendingBell.vue'
import { Separator } from '@/components/ui/separator'
import {
  Sidebar,
  SidebarContent,
  SidebarFooter,
  SidebarGroup,
  SidebarGroupContent,
  SidebarHeader,
  SidebarInset,
  SidebarMenu,
  SidebarMenuButton,
  SidebarMenuItem,
  SidebarProvider,
  SidebarRail,
  SidebarTrigger,
} from '@/components/ui/sidebar'
import { connectLive, disconnectLive } from '@/realtime/live'
import { useAuthStore } from '@/stores/auth'
import { useRunsStore } from '@/stores/runs'
import { computed, onMounted, onUnmounted } from 'vue'
import { RouterLink, RouterView, useRoute, useRouter } from 'vue-router'
import { type NavigationItem, navigation } from './navigation'

const auth = useAuthStore()
const route = useRoute()
const router = useRouter()

// Live updates and the pending-confirmations bell are only needed while signed in, i.e. inside the shell.
onMounted(() => {
  void useRunsStore().fetchRecent().catch(() => {})
  void connectLive()
})
onUnmounted(() => void disconnectLive())

const items = computed(() => navigation.filter((item) => !item.adminOnly || auth.isAdmin))

function isActive(item: NavigationItem) {
  const name = String(route.name)
  return router.resolve(item.to).name === name || (item.matches?.includes(name) ?? false)
}
</script>

<template>
  <SidebarProvider>
    <Sidebar collapsible="icon">
      <SidebarHeader>
        <SidebarMenu>
          <SidebarMenuItem>
            <SidebarMenuButton size="lg" as-child>
              <RouterLink :to="{ name: 'dashboard' }">
                <AppLogo show-name />
              </RouterLink>
            </SidebarMenuButton>
          </SidebarMenuItem>
        </SidebarMenu>
      </SidebarHeader>

      <SidebarContent>
        <SidebarGroup>
          <SidebarGroupContent>
            <SidebarMenu>
              <SidebarMenuItem v-for="item in items" :key="item.labelKey">
                <SidebarMenuButton as-child :is-active="isActive(item)" :tooltip="$t(item.labelKey)">
                  <RouterLink :to="item.to">
                    <component :is="item.icon" />
                    <span>{{ $t(item.labelKey) }}</span>
                  </RouterLink>
                </SidebarMenuButton>
              </SidebarMenuItem>
            </SidebarMenu>
          </SidebarGroupContent>
        </SidebarGroup>
      </SidebarContent>

      <SidebarFooter>
        <UserMenu />
      </SidebarFooter>
      <SidebarRail />
    </Sidebar>

    <SidebarInset>
      <header class="bg-background sticky top-0 z-10 flex h-14 shrink-0 items-center gap-2 border-b px-4">
        <SidebarTrigger class="-ml-1" :aria-label="$t('nav.toggleSidebar')" />
        <Separator orientation="vertical" class="mr-2 data-[orientation=vertical]:h-4" />
        <h1 class="truncate text-sm font-medium">{{ route.meta.titleKey ? $t(route.meta.titleKey) : '' }}</h1>
        <div class="ml-auto flex items-center gap-1">
          <PendingBell />
          <LanguageSwitcher />
          <ThemeToggle />
        </div>
      </header>
      <main class="flex-1 p-4 md:p-6">
        <RouterView />
      </main>
    </SidebarInset>
  </SidebarProvider>
</template>
