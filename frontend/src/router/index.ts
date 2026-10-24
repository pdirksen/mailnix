import { useAuthStore } from '@/stores/auth'
import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router'
import { safeRedirect } from './safeRedirect'

declare module 'vue-router' {
  interface RouteMeta {
    /** Only for signed-in users (inherited by child routes). */
    requiresAuth?: boolean
    /** Only for admins (inherited by child routes). */
    requiresAdmin?: boolean
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
  if (to.name === 'register' && !auth.registrationOpen) {
    return { name: 'login' }
  }
  return true
})
