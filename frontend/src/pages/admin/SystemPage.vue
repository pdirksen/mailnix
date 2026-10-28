<script setup lang="ts">
import { api, ApiError, type Schemas, unwrap } from '@/api/client'
import KpiTile from '@/components/stats/KpiTile.vue'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Skeleton } from '@/components/ui/skeleton'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { useConnectionErrors } from '@/lib/connectionErrors'
import { formatDateTime, formatRelative } from '@/lib/format'
import { CircleAlertIcon, CircleCheckIcon, Loader2Icon, MailIcon, RefreshCwIcon, SendIcon } from '@lucide/vue'
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

// Email: send a test message to the signed-in administrator; failures are shown with the server's reason.
const sendingTest = ref(false)
const testError = ref<string>()
async function sendTestEmail() {
  sendingTest.value = true
  testError.value = undefined
  try {
    unwrap(await api.POST('/api/system/test-email'))
    toast.success(t('system.email.testSent'))
  } catch (error) {
    testError.value = error instanceof ApiError ? (error.problem.detail ?? error.message) : describeRequestError(error).message
  } finally {
    sendingTest.value = false
  }
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
        <Badge variant="outline" data-testid="version">{{ $t('system.version', { version: system.version }) }}</Badge>
      </div>

      <div class="grid grid-cols-2 gap-4 lg:grid-cols-4">
        <KpiTile :label="$t('system.mailboxes')" :value="system.accounts" test-id="kpi-mailboxes" />
        <KpiTile :label="$t('system.unleased')" :value="system.unleasedAccounts" test-id="kpi-unleased" />
        <KpiTile :label="$t('system.queued')" :value="system.queuedRuns" test-id="kpi-queued" />
        <KpiTile :label="$t('system.awaiting')" :value="system.awaitingConfirmation" test-id="kpi-awaiting" />
      </div>

      <Card data-testid="email-status">
        <CardHeader>
          <CardTitle class="flex items-center gap-2"><MailIcon class="size-4" />{{ $t('system.email.title') }}</CardTitle>
          <CardDescription>{{ $t('system.email.description') }}</CardDescription>
        </CardHeader>
        <CardContent class="grid gap-4">
          <dl class="grid gap-3 text-sm sm:grid-cols-2">
            <div class="flex items-start gap-3 rounded-lg border px-3 py-2.5">
              <CircleCheckIcon v-if="system.email.configured" class="text-success mt-0.5 size-4 shrink-0" />
              <CircleAlertIcon v-else class="text-warning mt-0.5 size-4 shrink-0" />
              <div class="min-w-0">
                <dt class="font-medium">{{ $t('system.email.server') }}</dt>
                <dd class="text-muted-foreground truncate">
                  {{ system.email.configured ? $t('system.email.sendsAs', { from: system.email.from }) : $t('system.email.notConfigured') }}
                </dd>
              </div>
            </div>
            <div class="flex items-start gap-3 rounded-lg border px-3 py-2.5">
              <CircleCheckIcon v-if="system.email.publicUrl" class="text-success mt-0.5 size-4 shrink-0" />
              <CircleAlertIcon v-else class="text-warning mt-0.5 size-4 shrink-0" />
              <div class="min-w-0">
                <dt class="font-medium">{{ $t('system.email.publicUrl') }}</dt>
                <dd class="text-muted-foreground truncate">{{ system.email.publicUrl ?? $t('system.email.publicUrlMissing') }}</dd>
              </div>
            </div>
          </dl>
          <div class="flex flex-wrap items-center gap-3">
            <Button variant="outline" :disabled="!system.email.configured || sendingTest" data-testid="send-test-email" @click="sendTestEmail">
              <Loader2Icon v-if="sendingTest" class="animate-spin" />
              <SendIcon v-else />
              {{ $t('system.email.sendTest') }}
            </Button>
            <span class="text-muted-foreground text-xs">{{ $t('system.email.configHint') }}</span>
          </div>
          <p v-if="testError" class="text-destructive text-sm" role="alert" data-testid="test-email-error">{{ testError }}</p>
        </CardContent>
      </Card>

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
