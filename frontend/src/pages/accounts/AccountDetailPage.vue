<script setup lang="ts">
import AccountFolders from '@/components/accounts/AccountFolders.vue'
import AccountOverview from '@/components/accounts/AccountOverview.vue'
import AccountSettingsForm from '@/components/accounts/AccountSettingsForm.vue'
import AccountStatusBadge from '@/components/accounts/AccountStatusBadge.vue'
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
  AlertDialogTrigger,
} from '@/components/ui/alert-dialog'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { ApiError } from '@/api/client'
import { useConnectionErrors } from '@/lib/connectionErrors'
import { useAccountsStore } from '@/stores/accounts'
import { ArrowLeftIcon, Loader2Icon, PlugZapIcon, Trash2Icon } from '@lucide/vue'
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterLink, useRoute, useRouter } from 'vue-router'
import { toast } from 'vue-sonner'

const props = defineProps<{ id: string }>()

const TABS = ['overview', 'folders', 'settings'] as const
type Tab = (typeof TABS)[number]

const { t } = useI18n()
const store = useAccountsStore()
const route = useRoute()
const router = useRouter()
const { describe, describeRequestError } = useConnectionErrors()

const account = computed(() => store.accounts.find((a) => a.id === props.id))
const loading = ref(!account.value)
const notFound = ref(false)

const tab = computed<Tab>({
  get: () => (TABS.includes(route.query.tab as Tab) ? (route.query.tab as Tab) : 'overview'),
  set: (value) => void router.replace({ query: { ...route.query, tab: value === 'overview' ? undefined : value } }),
})

onMounted(async () => {
  try {
    await store.fetchOne(props.id)
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) notFound.value = true
    else toast.error(describeRequestError(error).message)
  } finally {
    loading.value = false
  }
})

const testing = ref(false)
async function testConnection() {
  testing.value = true
  try {
    const result = await store.testStored(props.id)
    if (result.success) toast.success(t('accounts.testOk'))
    else toast.error(t('accounts.testFailed'), { description: describe(result.errorCode, result.errorMessage).message })
  } catch (error) {
    toast.error(t('accounts.testFailed'), { description: describeRequestError(error).message })
  } finally {
    testing.value = false
  }
}

async function deleteAccount() {
  try {
    await store.remove(props.id)
    toast.success(t('accounts.deleted'))
    await router.push({ name: 'accounts' })
  } catch (error) {
    toast.error(describeRequestError(error).message)
  }
}
</script>

<template>
  <div class="mx-auto grid max-w-5xl gap-6">
    <RouterLink :to="{ name: 'accounts' }" class="text-muted-foreground hover:text-foreground flex w-fit items-center gap-1 text-sm">
      <ArrowLeftIcon class="size-4" />
      {{ $t('accounts.title') }}
    </RouterLink>

    <p v-if="notFound" class="text-muted-foreground">{{ $t('accounts.notFound') }}</p>
    <Skeleton v-else-if="loading && !account" class="h-24" />

    <template v-else-if="account">
      <div class="flex flex-wrap items-start justify-between gap-4">
        <div class="min-w-0">
          <div class="flex flex-wrap items-center gap-3">
            <h2 class="truncate text-2xl font-semibold tracking-tight">{{ account.name }}</h2>
            <AccountStatusBadge :status="account.status" :enabled="account.enabled" />
          </div>
          <p class="text-muted-foreground mt-1 truncate">{{ account.username }} · {{ account.host }}</p>
        </div>
        <div class="flex gap-2">
          <Button variant="outline" :disabled="testing" data-testid="test-connection" @click="testConnection">
            <Loader2Icon v-if="testing" class="animate-spin" />
            <PlugZapIcon v-else />
            {{ $t('accounts.test') }}
          </Button>
          <AlertDialog>
            <AlertDialogTrigger as-child>
              <Button variant="outline" class="text-destructive" :aria-label="$t('accounts.delete')">
                <Trash2Icon />
                <span class="hidden sm:inline">{{ $t('accounts.delete') }}</span>
              </Button>
            </AlertDialogTrigger>
            <AlertDialogContent>
              <AlertDialogHeader>
                <AlertDialogTitle>{{ $t('accounts.deleteTitle', { name: account.name }) }}</AlertDialogTitle>
                <AlertDialogDescription>{{ $t('accounts.deleteText') }}</AlertDialogDescription>
              </AlertDialogHeader>
              <AlertDialogFooter>
                <AlertDialogCancel>{{ $t('common.cancel') }}</AlertDialogCancel>
                <AlertDialogAction class="bg-destructive hover:bg-destructive/90 text-white" @click="deleteAccount">
                  {{ $t('accounts.delete') }}
                </AlertDialogAction>
              </AlertDialogFooter>
            </AlertDialogContent>
          </AlertDialog>
        </div>
      </div>

      <Tabs v-model="tab">
        <TabsList>
          <TabsTrigger v-for="name in TABS" :key="name" :value="name">{{ $t(`accounts.tabs.${name}`) }}</TabsTrigger>
        </TabsList>
        <TabsContent value="overview" class="mt-4">
          <AccountOverview :account="account" />
        </TabsContent>
        <TabsContent value="folders" class="mt-4">
          <AccountFolders :account-id="account.id" />
        </TabsContent>
        <TabsContent value="settings" class="mt-4">
          <AccountSettingsForm :key="account.id" :account="account" />
        </TabsContent>
      </Tabs>
    </template>
  </div>
</template>
