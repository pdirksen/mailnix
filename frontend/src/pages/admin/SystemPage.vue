<script setup lang="ts">
import { api, type Schemas, unwrap } from '@/api/client'
import KpiTile from '@/components/stats/KpiTile.vue'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Skeleton } from '@/components/ui/skeleton'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { useConnectionErrors } from '@/lib/connectionErrors'
import { formatDateTime, formatRelative } from '@/lib/format'
import { RefreshCwIcon } from '@lucide/vue'
import { useIntervalFn } from '@vueuse/core'
import { onMounted, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { toast } from 'vue-sonner'

const { t, locale } = useI18n()
const { describeRequestError } = useConnectionErrors()

const system = ref<Schemas['SystemResponse']>()
const loading = ref(false)
/** Edited mailbox limits per tenant ('' = unlimited). */
const limits = reactive<Record<string, string>>({})

async function load() {
  loading.value = true
  try {
    system.value = unwrap(await api.GET('/api/system'))
    for (const tenant of system.value.tenants) limits[tenant.id] = tenant.maxAccounts?.toString() ?? ''
  } catch (error) {
    toast.error(describeRequestError(error).message)
  } finally {
    loading.value = false
  }
}

onMounted(load)
// Workers come and go; keep the overview current without reloading the tenant limits being edited.
useIntervalFn(async () => {
  const result = await api.GET('/api/system').catch(() => undefined)
  if (result?.data && system.value) system.value = { ...result.data, tenants: system.value.tenants }
}, 15_000)

function limitValid(tenantId: string) {
  const value = limits[tenantId]?.trim() ?? ''
  return value === '' || (/^\d+$/.test(value) && Number(value) >= 1 && Number(value) <= 100_000)
}

function limitChanged(tenant: Schemas['TenantRow']) {
  return (limits[tenant.id]?.trim() ?? '') !== (tenant.maxAccounts?.toString() ?? '')
}

async function saveLimit(tenant: Schemas['TenantRow']) {
  if (!limitValid(tenant.id)) return
  const value = limits[tenant.id]?.trim() ?? ''
  try {
    unwrap(
      await api.PUT('/api/system/tenants/{id}', {
        params: { path: { id: tenant.id } },
        body: { maxAccounts: value === '' ? null : Number(value) },
      }),
    )
    tenant.maxAccounts = value === '' ? null : Number(value)
    toast.success(t('system.limitSaved', { name: tenant.name }))
  } catch (error) {
    toast.error(describeRequestError(error).message)
  }
}
</script>

<template>
  <div class="mx-auto grid max-w-6xl gap-6">
    <div class="flex flex-wrap items-end justify-between gap-4">
      <div>
        <h2 class="text-2xl font-semibold tracking-tight">{{ $t('system.title') }}</h2>
        <p class="text-muted-foreground mt-1 max-w-2xl">{{ $t('system.subtitle') }}</p>
      </div>
      <Button variant="outline" :disabled="loading" @click="load">
        <RefreshCwIcon :class="{ 'animate-spin': loading }" />
        {{ $t('common.refresh') }}
      </Button>
    </div>

    <template v-if="!system">
      <div class="grid grid-cols-2 gap-4 lg:grid-cols-4">
        <Skeleton v-for="i in 4" :key="i" class="h-24 rounded-xl" />
      </div>
      <Skeleton class="h-48 rounded-xl" />
    </template>

    <template v-else>
      <div class="flex flex-wrap gap-2 text-sm">
        <Badge variant="secondary" data-testid="run-mode">{{ $t(`system.modes.${system.mode}`) }}</Badge>
        <Badge variant="outline">{{ system.multiTenant ? $t('system.multiTenant') : $t('system.singleTenant') }}</Badge>
      </div>

      <div class="grid grid-cols-2 gap-4 lg:grid-cols-4">
        <KpiTile :label="$t('system.mailboxes')" :value="system.accounts" test-id="kpi-mailboxes" />
        <KpiTile :label="$t('system.unleased')" :value="system.unleasedAccounts" test-id="kpi-unleased" />
        <KpiTile :label="$t('system.queued')" :value="system.queuedRuns" test-id="kpi-queued" />
        <KpiTile :label="$t('system.awaiting')" :value="system.awaitingConfirmation" test-id="kpi-awaiting" />
      </div>

      <Card>
        <CardHeader>
          <CardTitle>{{ $t('system.workers') }}</CardTitle>
          <CardDescription>{{ $t('system.workersHint') }}</CardDescription>
        </CardHeader>
        <CardContent class="overflow-x-auto">
          <p v-if="system.workers.length === 0" class="text-muted-foreground text-sm">{{ $t('system.noWorkers') }}</p>
          <Table v-else data-testid="workers">
            <TableHeader>
              <TableRow>
                <TableHead>{{ $t('system.worker') }}</TableHead>
                <TableHead>{{ $t('system.status') }}</TableHead>
                <TableHead class="text-right">{{ $t('system.leases') }}</TableHead>
                <TableHead>{{ $t('system.started') }}</TableHead>
                <TableHead>{{ $t('system.lastSeen') }}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              <TableRow v-for="worker in system.workers" :key="worker.id">
                <TableCell>
                  <div class="font-medium">{{ worker.host }}</div>
                  <div class="text-muted-foreground font-mono text-xs">{{ worker.id }}</div>
                </TableCell>
                <TableCell>
                  <Badge :variant="worker.alive ? 'secondary' : 'destructive'">
                    {{ worker.alive ? $t('system.alive') : $t('system.gone') }}
                  </Badge>
                </TableCell>
                <TableCell class="text-right tabular-nums">{{ worker.leases }}</TableCell>
                <TableCell class="whitespace-nowrap">{{ formatDateTime(worker.startedAt, locale) }}</TableCell>
                <TableCell class="whitespace-nowrap">{{ formatRelative(worker.lastSeenAt, locale) }}</TableCell>
              </TableRow>
            </TableBody>
          </Table>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>{{ $t('system.organizations') }}</CardTitle>
          <CardDescription>{{ $t('system.organizationsHint') }}</CardDescription>
        </CardHeader>
        <CardContent class="overflow-x-auto">
          <Table data-testid="tenants">
            <TableHeader>
              <TableRow>
                <TableHead>{{ $t('common.name') }}</TableHead>
                <TableHead class="text-right">{{ $t('system.users') }}</TableHead>
                <TableHead class="text-right">{{ $t('system.mailboxes') }}</TableHead>
                <TableHead>{{ $t('system.limit') }}</TableHead>
                <TableHead>{{ $t('system.created') }}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              <TableRow v-for="tenant in system.tenants" :key="tenant.id" :data-testid="`tenant-${tenant.name}`">
                <TableCell class="max-w-72 truncate font-medium">{{ tenant.name }}</TableCell>
                <TableCell class="text-right tabular-nums">{{ tenant.users }}</TableCell>
                <TableCell class="text-right tabular-nums">{{ tenant.accounts }}</TableCell>
                <TableCell>
                  <form novalidate class="flex items-center gap-2" @submit.prevent="saveLimit(tenant)">
                    <Input
                      v-model="limits[tenant.id]"
                      inputmode="numeric"
                      class="w-28"
                      :placeholder="$t('system.unlimited')"
                      :aria-label="$t('system.limitFor', { name: tenant.name })"
                      :aria-invalid="!limitValid(tenant.id)"
                    />
                    <Button v-if="limitChanged(tenant)" type="submit" size="sm" variant="outline" :disabled="!limitValid(tenant.id)">
                      {{ $t('common.save') }}
                    </Button>
                  </form>
                </TableCell>
                <TableCell class="whitespace-nowrap">{{ formatDateTime(tenant.createdAt, locale) }}</TableCell>
              </TableRow>
            </TableBody>
          </Table>
        </CardContent>
      </Card>
    </template>
  </div>
</template>
