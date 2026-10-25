<script setup lang="ts">
import AccountStatusBadge from '@/components/accounts/AccountStatusBadge.vue'
import ActionsList from '@/components/runs/ActionsList.vue'
import BarList from '@/components/stats/BarList.vue'
import { bucketEnd } from '@/components/stats/buckets'
import ChartCard from '@/components/stats/ChartCard.vue'
import KpiTile from '@/components/stats/KpiTile.vue'
import SeriesTable from '@/components/stats/SeriesTable.vue'
import TimeSeriesChart from '@/components/stats/TimeSeriesChart.vue'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { GROUP_ACTION, type ActionGroup, useDarkMode } from '@/lib/charts'
import { useConnectionErrors } from '@/lib/connectionErrors'
import { formatRelative } from '@/lib/format'
import { groupByAction, type Overview, statsApi, useActionGroupStyle } from '@/lib/stats'
import { useAuthStore } from '@/stores/auth'
import { useRunsStore } from '@/stores/runs'
import { LightbulbIcon } from '@lucide/vue'
import { useDebounceFn } from '@vueuse/core'
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterLink, useRoute, useRouter } from 'vue-router'

const ALL = 'all'
const auth = useAuthStore()
const runs = useRunsStore()
const route = useRoute()
const router = useRouter()
const { locale } = useI18n()
const { describeRequestError } = useConnectionErrors()
const dark = useDarkMode()
const groupStyle = useActionGroupStyle(dark)

const accountId = computed({
  get: () => (typeof route.query.account === 'string' ? route.query.account : undefined),
  set: (value?: string) =>
    void router.replace({ query: { ...route.query, account: value && value !== ALL ? value : undefined } }),
})

const overview = ref<Overview | null>(null)
const loading = ref(false)
const error = ref<string | null>(null)
const refreshKey = ref(0)

async function load() {
  loading.value = true
  try {
    overview.value = await statsApi.overview(accountId.value)
    error.value = null
  } catch (e) {
    error.value = describeRequestError(e).message
  } finally {
    loading.value = false
  }
}

watch(accountId, () => void load(), { immediate: true })

// Live: runs reported over the live connection update the numbers and the feed (debounced, keeps the frame).
const refresh = useDebounceFn(() => {
  refreshKey.value++
  void load()
}, 1000)
watch(
  () => runs.runs.map((r) => `${r.id}:${r.state}:${r.executedActions}`).join(),
  () => void refresh(),
)

const activity = computed(() => (overview.value ? groupByAction(overview.value.activity) : null))
const accountOptions = computed(() => overview.value?.accounts ?? [])

function openDay(bucket: string, group: string) {
  void router.push({
    name: 'activity',
    query: {
      view: 'actions',
      from: bucket,
      to: bucketEnd(bucket, 'Day'),
      account: accountId.value,
      action: GROUP_ACTION[group as ActionGroup],
    },
  })
}
</script>

<template>
  <div class="mx-auto grid max-w-6xl gap-6">
    <div class="flex flex-wrap items-end justify-between gap-4">
      <div>
        <h2 class="text-2xl font-semibold tracking-tight">{{ $t('home.welcome', { name: auth.displayName }) }}</h2>
        <p class="text-muted-foreground mt-1">{{ $t('dashboard.subtitle') }}</p>
      </div>
      <div v-if="accountOptions.length > 1 || accountId" class="flex items-center gap-2">
        <Label for="dashboard-account">{{ $t('activity.account') }}</Label>
        <Select :model-value="accountId ?? ALL" @update:model-value="(v) => (accountId = String(v))">
          <SelectTrigger id="dashboard-account" class="w-56"><SelectValue /></SelectTrigger>
          <SelectContent>
            <SelectItem :value="ALL">{{ $t('activity.allAccounts') }}</SelectItem>
            <SelectItem v-for="account in accountOptions" :key="account.id" :value="account.id">{{
              account.name
            }}</SelectItem>
          </SelectContent>
        </Select>
      </div>
    </div>

    <p v-if="error" class="text-destructive text-sm" role="alert">{{ error }}</p>

    <!-- Key numbers: today, compared with yesterday -->
    <section class="grid grid-cols-2 gap-3 md:grid-cols-4" :aria-label="$t('dashboard.today')">
      <KpiTile
        test-id="kpi-processed"
        :label="$t('dashboard.kpis.processed')"
        :value="overview?.todayKpis.processed ?? null"
        :previous="overview?.yesterdayKpis.processed"
        :previous-label="$t('dashboard.vsYesterday')"
      />
      <KpiTile
        test-id="kpi-actions"
        :label="$t('dashboard.kpis.actions')"
        :value="overview?.todayKpis.actions ?? null"
        :previous="overview?.yesterdayKpis.actions"
        :previous-label="$t('dashboard.vsYesterday')"
      />
      <KpiTile
        :label="$t('dashboard.kpis.moved')"
        :value="overview?.todayKpis.moved ?? null"
        :previous="overview?.yesterdayKpis.moved"
        :previous-label="$t('dashboard.vsYesterday')"
      />
      <KpiTile
        :label="$t('dashboard.kpis.trashed')"
        :value="overview?.todayKpis.trashed ?? null"
        :previous="overview?.yesterdayKpis.trashed"
        :previous-label="$t('dashboard.vsYesterday')"
      />
      <KpiTile :label="$t('dashboard.kpis.unread')" :value="overview?.unread ?? null" />
      <KpiTile
        test-id="kpi-pending"
        :label="$t('dashboard.kpis.pending')"
        :value="overview?.pendingConfirmations ?? null"
        :to="
          overview?.pendingConfirmations ? { name: 'activity', query: { state: 'AwaitingConfirmation' } } : undefined
        "
      />
      <KpiTile
        :label="$t('dashboard.kpis.accountErrors')"
        :value="overview?.accountsWithErrors ?? null"
        :to="overview?.accountsWithErrors ? { name: 'accounts' } : undefined"
      />
    </section>

    <!-- Hints -->
    <section v-if="overview?.hints.length" class="grid gap-2" data-testid="hints">
      <div
        v-for="(hint, i) in overview.hints"
        :key="i"
        class="bg-muted/50 flex flex-wrap items-center gap-3 rounded-lg border px-4 py-3 text-sm"
      >
        <LightbulbIcon class="text-muted-foreground size-4 shrink-0" />
        <span class="flex-1">{{
          $t(`dashboard.hints.${hint.kind}`, { rule: hint.ruleName ?? '', count: hint.count ?? 0 })
        }}</span>
        <Button v-if="hint.ruleId" size="sm" variant="outline" as-child>
          <RouterLink :to="{ name: 'rule-edit', params: { id: hint.ruleId } }">{{
            $t('dashboard.openRule')
          }}</RouterLink>
        </Button>
        <Button v-else size="sm" variant="outline" as-child>
          <RouterLink :to="{ name: 'rules' }">{{ $t('dashboard.toRules') }}</RouterLink>
        </Button>
      </div>
    </section>

    <!-- Accounts -->
    <section v-if="overview && overview.accounts.length > 0" class="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
      <RouterLink
        v-for="account in overview.accounts"
        :key="account.id"
        :to="{ name: 'account-detail', params: { id: account.id } }"
        class="block"
      >
        <Card class="hover:bg-accent/40 h-full gap-2 py-4 transition-colors">
          <CardContent class="grid gap-1 text-sm">
            <div class="flex items-center justify-between gap-2">
              <span class="truncate font-medium">{{ account.name }}</span>
              <AccountStatusBadge :status="account.status" :enabled="account.enabled" />
            </div>
            <span class="text-muted-foreground">
              {{ $t('dashboard.accountToday', { processed: account.processedToday, actions: account.actionsToday }) }}
              <template v-if="account.lastCheckedAt"> · {{ formatRelative(account.lastCheckedAt, locale) }}</template>
            </span>
            <span v-if="account.lastErrorCode" class="text-destructive text-xs">{{
              $t(`connectionErrors.${account.lastErrorCode}`)
            }}</span>
          </CardContent>
        </Card>
      </RouterLink>
    </section>
    <Card v-else-if="overview" class="py-8 text-center">
      <CardContent class="grid justify-items-center gap-3">
        <p class="text-muted-foreground">{{ $t('dashboard.noAccounts') }}</p>
        <Button as-child
          ><RouterLink :to="{ name: 'account-new' }">{{ $t('accounts.add') }}</RouterLink></Button
        >
      </CardContent>
    </Card>

    <div class="grid gap-6 lg:grid-cols-3">
      <ChartCard
        class="lg:col-span-2"
        :title="$t('dashboard.activityTitle')"
        :description="$t('dashboard.activityDescription')"
        :loading="loading"
        :loaded="!!overview"
        :empty="!!activity && activity.series.length === 0"
        table
      >
        <TimeSeriesChart
          v-if="activity"
          :data="activity"
          kind="stack"
          granularity="Day"
          :style-of="groupStyle"
          @select="openDay"
        />
        <template #table>
          <SeriesTable v-if="activity" :data="activity" granularity="Day" :style-of="groupStyle" />
        </template>
      </ChartCard>

      <Card class="gap-4">
        <CardHeader
          ><CardTitle class="text-base">{{ $t('dashboard.topRules') }}</CardTitle></CardHeader
        >
        <CardContent>
          <p v-if="overview && overview.topRules.length === 0" class="text-muted-foreground text-sm">
            {{ $t('stats.noData') }}
          </p>
          <BarList
            v-else-if="overview"
            :items="
              overview.topRules.map((r) => ({
                key: r.ruleId,
                label: r.name,
                value: r.count,
                detail: $t('dashboard.messages'),
                to: { name: 'rule-edit', params: { id: r.ruleId } },
              }))
            "
          />
        </CardContent>
      </Card>
    </div>

    <Card class="gap-4">
      <CardHeader class="flex flex-row items-center justify-between">
        <CardTitle class="text-base">{{ $t('dashboard.recent') }}</CardTitle>
        <Button size="sm" variant="ghost" as-child>
          <RouterLink :to="{ name: 'activity', query: { view: 'actions', account: accountId } }">{{
            $t('dashboard.allActivity')
          }}</RouterLink>
        </Button>
      </CardHeader>
      <CardContent>
        <ActionsList :filters="{ accountId }" :limit="10" compact :refresh-key="refreshKey" />
      </CardContent>
    </Card>
  </div>
</template>
