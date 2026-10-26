import { useAuthStore } from '@/stores/auth'
import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router'
import { safeRedirect } from './safeRedirect'

declare module 'vue-router' {
  interface RouteMeta {
    /** Only for signed-in users (inherited by child routes). */
    requiresAuth?: boolean
    /** Only for admins of the installation (inherited by child routes). */
    requiresAdmin?: boolean
    /** Only for admins of the user's organization (or of the installation). */
    requiresTenantAdmin?: boolean
    /** Only for signed-out users, e.g. login. */
    guestOnly?: boolean
    /** i18n key of the page title. */
    titleKey?: string
  }
}

// Both layouts are mounted at '/'. The app shell must come first: a parent route without a matching
// child still matches its own path, so '/' would otherwise resolve to the (empty) guest layout.
export const routes: RouteRecordRaw[] = [
  {
    path: '/',
    component: () => import('@/layouts/AppShell.vue'),
    meta: { requiresAuth: true },
    children: [
      {
        path: '',
        name: 'dashboard',
        component: () => import('@/pages/HomePage.vue'),
        meta: { titleKey: 'nav.dashboard' },
      },
      {
        path: 'accounts',
        name: 'accounts',
        component: () => import('@/pages/accounts/AccountsPage.vue'),
        meta: { titleKey: 'nav.accounts' },
      },
      {
        path: 'accounts/new',
        name: 'account-new',
        component: () => import('@/pages/accounts/AddAccountPage.vue'),
        meta: { titleKey: 'accounts.wizard.title' },
      },
      {
        path: 'accounts/:id',
        name: 'account-detail',
        component: () => import('@/pages/accounts/AccountDetailPage.vue'),
        props: true,
        meta: { titleKey: 'nav.accounts' },
      },
      {
        path: 'accounts/:id/setup',
        name: 'account-setup',
        component: () => import('@/pages/accounts/AccountSetupPage.vue'),
        props: true,
        meta: { titleKey: 'assistant.title' },
      },
      {
        path: 'rules',
        name: 'rules',
        component: () => import('@/pages/rules/RulesPage.vue'),
        meta: { titleKey: 'nav.rules' },
      },
      {
        path: 'rules/new',
        name: 'rule-new',
        component: () => import('@/pages/rules/RuleEditorPage.vue'),
        meta: { titleKey: 'rules.editor.newTitle' },
      },
      {
        path: 'rules/:id',
        name: 'rule-edit',
        component: () => import('@/pages/rules/RuleEditorPage.vue'),
        props: true,
        meta: { titleKey: 'rules.editor.editTitle' },
      },
      {
        path: 'lists',
        name: 'lists',
        component: () => import('@/pages/lists/ListsPage.vue'),
        meta: { titleKey: 'nav.lists' },
      },
      {
        path: 'lists/:id',
        name: 'list-detail',
        component: () => import('@/pages/lists/ListDetailPage.vue'),
        props: true,
        meta: { titleKey: 'nav.lists' },
      },
      {
        // Google / Microsoft return here after signing in (the redirect URI registered with them).
        path: 'oauth/callback',
        name: 'oauth-callback',
        component: () => import('@/pages/oauth/OAuthCallbackPage.vue'),
        meta: { titleKey: 'oauth.title' },
      },
      {
        path: 'stats',
        name: 'stats',
        component: () => import('@/pages/stats/StatsPage.vue'),
        meta: { titleKey: 'nav.stats' },
      },
      {
        path: 'activity',
        name: 'activity',
        component: () => import('@/pages/activity/ActivityPage.vue'),
        meta: { titleKey: 'nav.activity' },
      },
      {
        path: 'activity/pending/:id',
        name: 'run-review',
        component: () => import('@/pages/activity/RunReviewPage.vue'),
        props: true,
        meta: { titleKey: 'activity.reviewTitle' },
      },
      {
        path: 'settings',
        component: () => import('@/pages/settings/SettingsLayout.vue'),
        meta: { titleKey: 'nav.settings' },
        children: [
          { path: '', name: 'settings-profile', component: () => import('@/pages/settings/ProfileSettings.vue') },
          { path: 'preferences', name: 'settings-preferences', component: () => import('@/pages/settings/PreferencesSettings.vue') },
          { path: 'addresses', name: 'settings-addresses', component: () => import('@/pages/settings/AddressesSettings.vue') },
          { path: 'security', name: 'settings-security', component: () => import('@/pages/settings/SecuritySettings.vue') },
        ],
      },
      {
        path: 'admin/team',
        name: 'admin-team',
        component: () => import('@/pages/admin/TeamPage.vue'),
        meta: { titleKey: 'nav.team', requiresTenantAdmin: true },
      },
      {
        path: 'admin/system',
        name: 'admin-system',
        component: () => import('@/pages/admin/SystemPage.vue'),
        meta: { titleKey: 'nav.system', requiresAdmin: true },
      },
    ],
  },
  {
    path: '/',
    component: () => import('@/layouts/AuthLayout.vue'),
    meta: { guestOnly: true },
    children: [
      {
        path: 'login',
        name: 'login',
        component: () => import('@/pages/auth/LoginPage.vue'),
        meta: { titleKey: 'auth.login' },
      },
      {
        path: 'register',
        name: 'register',
        component: () => import('@/pages/auth/RegisterPage.vue'),
        meta: { titleKey: 'auth.register' },
      },
    ],
  },
  {
    path: '/:pathMatch(.*)*',
    name: 'not-found',
    component: () => import('@/pages/NotFoundPage.vue'),
    meta: { titleKey: 'notFound.title' },
  },
]

export const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  routes,
})

router.beforeEach(async (to) => {
  const auth = useAuthStore()
  await auth.initialize()

  if (to.matched.some((r) => r.meta.requiresAuth) && !auth.isAuthenticated) {
    return { name: 'login', query: to.fullPath === '/' ? {} : { redirect: to.fullPath } }
  }
  if (to.matched.some((r) => r.meta.guestOnly) && auth.isAuthenticated) {
    return safeRedirect(to.query.redirect)
  }
  if (to.matched.some((r) => r.meta.requiresAdmin) && !auth.isAdmin) {
    return { name: 'dashboard' }
  }
  if (to.matched.some((r) => r.meta.requiresTenantAdmin) && !auth.isTenantAdmin) {
    return { name: 'dashboard' }
  }
  // Invitations work even when signup is closed.
  if (to.name === 'register' && !auth.registrationOpen && !to.query.invite) {
    return { name: 'login' }
  }
  return true
})
