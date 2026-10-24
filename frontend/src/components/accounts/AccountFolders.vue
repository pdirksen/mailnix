<script setup lang="ts">
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { type DescribedError, useConnectionErrors } from '@/lib/connectionErrors'
import { type FolderList, useAccountsStore } from '@/stores/accounts'
import { RefreshCwIcon } from '@lucide/vue'
import { onMounted, ref } from 'vue'
import ConnectionErrorAlert from './ConnectionErrorAlert.vue'
import FolderTree from './FolderTree.vue'

const props = defineProps<{ accountId: string }>()

const store = useAccountsStore()
const { describeRequestError } = useConnectionErrors()
const folders = ref<FolderList | null>(null)
const loading = ref(false)
const error = ref<DescribedError | null>(null)

async function load() {
  loading.value = true
  error.value = null
  try {
    folders.value = await store.folders(props.accountId, true)
  } catch (e) {
    error.value = describeRequestError(e)
  } finally {
    loading.value = false
    // The server updates the account status on every folder listing.
    void store.fetchOne(props.accountId).catch(() => {})
  }
}

onMounted(load)
</script>

<template>
  <div class="grid gap-4">
    <div class="flex justify-end">
      <Button variant="outline" size="sm" :disabled="loading" @click="load">
        <RefreshCwIcon :class="{ 'animate-spin': loading }" />
        {{ $t('common.refresh') }}
      </Button>
    </div>
    <ConnectionErrorAlert v-if="error" :title="$t('accounts.folders.loadFailed')" :error="error" />
    <div v-else-if="loading && !folders" class="grid gap-2" role="status" :aria-label="$t('common.loading')">
      <Skeleton v-for="i in 6" :key="i" class="h-10" />
    </div>
    <FolderTree v-else-if="folders" :folders="folders.folders" />
  </div>
</template>
