import { LayoutDashboardIcon } from '@lucide/vue'
import type { Component } from 'vue'
import type { RouteLocationRaw } from 'vue-router'

export interface NavigationItem {
  to: RouteLocationRaw
  /** i18n key */
  labelKey: string
  icon: Component
  adminOnly?: boolean
}

/** Sidebar entries; each milestone adds its pages here. */
export const navigation: NavigationItem[] = [{ to: { name: 'dashboard' }, labelKey: 'nav.dashboard', icon: LayoutDashboardIcon }]
