import 'vue-sonner/style.css'
import './assets/main.css'

import { setUnauthorizedHandler } from '@/api/client'
import { i18n } from '@/i18n'
import { router } from '@/router'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import { createPinia } from 'pinia'
import { createApp } from 'vue'
import App from './App.vue'

const app = createApp(App)
app.use(createPinia())
app.use(i18n)
app.use(router)

useUiStore() // applies persisted theme and language before the first render

setUnauthorizedHandler(() => {
  useAuthStore().clear()
  const current = router.currentRoute.value
  if (current.name !== 'login') {
    void router.push({ name: 'login', query: { redirect: current.fullPath, expired: '1' } })
  }
})

app.mount('#app')
