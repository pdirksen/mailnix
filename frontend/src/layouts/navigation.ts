import { LayoutDashboardIcon, ListChecksIcon, MailIcon } from '@lucide/vue'
import type { Component } from 'vue'
import type { RouteLocationRaw } from 'vue-router'

export interface NavigationItem {
  to: RouteLocationRaw
  /** i18n key */
  labelKey: string
  icon: Component
  adminOnly?: boolean
  /** Other route names that highlight this entry (e.g. detail pages). */
  matches?: string[]
}

/** Sidebar entries; each milestone adds its pages here. */
export const navigation: NavigationItem[] = [
  { to: { name: 'dashboard' }, labelKey: 'nav.dashboard', icon: LayoutDashboardIcon },
  { to: { name: 'accounts' }, labelKey: 'nav.accounts', icon: MailIcon, matches: ['account-new', 'account-detail'] },
  { to: { name: 'lists' }, labelKey: 'nav.lists', icon: ListChecksIcon, matches: ['list-detail'] },
]
