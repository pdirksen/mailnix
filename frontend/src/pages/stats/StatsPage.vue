<script setup lang="ts">
import type { Schemas } from '@/api/client'
import BarList from '@/components/stats/BarList.vue'
import { bucketEnd, type Granularity } from '@/components/stats/buckets'
import ChartCard from '@/components/stats/ChartCard.vue'
import KpiTile from '@/components/stats/KpiTile.vue'
import SeriesTable from '@/components/stats/SeriesTable.vue'
import TimeSeriesChart, { type SeriesStyle, type TimeSeries } from '@/components/stats/TimeSeriesChart.vue'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { GROUP_ACTION, type ActionGroup, slotColor, useDarkMode } from '@/lib/charts'
import { useConnectionErrors } from '@/lib/connectionErrors'
import { exportUrl, groupByAction, RANGE_PRESETS, statsApi, useActionGroupStyle, useStatsFilters } from '@/lib/stats'
import { useAccountsStore } from '@/stores/accounts'
import { useRunsStore } from '@/stores/runs'
import { RefreshCwIcon } from '@lucide/vue'
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterLink, useRouter } from 'vue-router'

const ALL = 'all'
const { t, locale } = useI18n()
const router = useRouter()
const accounts = useAccountsStore()
const runs = useRunsStore()
const { describeRequestError } = useConnectionErrors()
const dark = useDarkMode()
const groupStyle = useActionGroupStyle(dark)
const { range, accountId, granularity, params } = useStatsFilters()

onMounted(() => void accounts.fetchAll().catch(() => {}))

interface Section<T> {
  data: T | null
  loading: boolean
  error: string | null
}

function section<T>(): Section<T> {
  return reactive({ data: null, loading: false, error: null }) as Section<T>
}

const volume = section<{ totals: TimeSeries; byAction: TimeSeries }>()
const rules = section<Schemas['RuleStatsResponse']>()
const senders = section<Schemas['SendersResponse']>()
const folders = section<Schemas['FoldersResponse']>()
const security = section<Schemas['SecurityResponse']>()
const reliability = section<Schemas['ReliabilityResponse']>()

async function fill<T>(target: Section<T>, load: () => Promise<T>) {
  target.loading = true
  try {
    target.data = await load()
    target.error = null
  } catch (e) {
    target.error = describeRequestError(e).message
  } finally {
    target.loading = false
  }
}

const newData = ref(false)

function loadAll() {
  newData.value = false
  const p = params.value
  void fill(volume, async () => {
    const [processed, actions, byAction] = await Promise.all([
      statsApi.timeSeries({ ...p, metric: 'processed', groupBy: 'none' }),
      statsApi.timeSeries({ ...p, metric: 'actions', groupBy: 'none' }),
      statsApi.timeSeries({ ...p, metric: 'actions', groupBy: 'action' }),
    ])
    const values = (data: TimeSeries) => data.series[0]?.values ?? data.buckets.map(() => 0)
    return {
      totals: {
        buckets: processed.buckets,
        series: [
          { key: 'processed', name: null, values: values(processed) },
          { key: 'actions', name: null, values: values(actions) },
        ],
      },
      byAction: groupByAction(byAction),
    }
  })
  void fill(rules, () => statsApi.rules(p))
  void fill(senders, () => statsApi.senders(p))
  void fill(folders, () => statsApi.folders({ accountId: p.accountId }))
  void fill(security, () => statsApi.security(p))
  void fill(reliability, () => statsApi.reliability(p))
}

watch(params, loadAll, { immediate: true, deep: true })

// Live updates only announce new data: charts do not reflow under the reader.
watch(
  () => runs.runs.map((r) => `${r.id}:${r.state}`).join(),
  (now, before) => {
    if (before !== undefined && now !== before) newData.value = true
  },
)

/** Fixed colors per series key, independent of what is shown. */
const KEY_SLOTS: Record<string, number> = {
  processed: 0,
  actions: 1,
  messages: 0,
  unread: 1,
  spf: 0,
  dkim: 1,
  dmarc: 2,
  failed: 1,
}
const keyStyle = (key: string): SeriesStyle => ({
  label: t(`stats.keys.${key}`),
  color: slotColor(KEY_SLOTS[key] ?? 0, dark.value),
})

const authFailures = computed<TimeSeries | null>(() =>
  security.data
    ? {
        buckets: security.data.series.buckets,
        series: security.data.series.series.filter((s) => ['spf', 'dkim', 'dmarc'].includes(s.key)),
      }
    : null,
)
// Messages only: unread (a far smaller number) would flatten to zero on the same axis; the table has it per folder.
const mailboxSize = computed<TimeSeries | null>(() =>
  folders.data
    ? { buckets: folders.data.growth.buckets, series: folders.data.growth.series.filter((s) => s.key === 'messages') }
    : null,
)
const failedActions = computed<TimeSeries | null>(() =>
  reliability.data
    ? {
        buckets: reliability.data.failedActions.buckets,
        series: reliability.data.failedActions.series.map((s) => ({ ...s, key: 'failed' })),
      }
    : null,
)
const hasValues = (data: TimeSeries | null | undefined) =>
  !!data && data.series.some((s) => s.values.some((v) => v > 0))

/** The action to filter the activity by for an action group (only groups of one action have one). */
const actionOf = (group: string) => GROUP_ACTION[group as ActionGroup]

function drillDown(bucket: string, extra: Record<string, string | undefined> = {}) {
  void router.push({
    name: 'activity',
    query: {
      view: 'actions',
      from: bucket,
      to: bucketEnd(bucket, granularity.value),
      account: accountId.value,
      ...extra,
    },
  })
}

// Rules table: sortable columns.
type RuleSort = 'name' | 'matched' | 'actions' | 'failed' | 'undone' | 'lastMatch'
const sort = ref<{ by: RuleSort; desc: boolean }>({ by: 'matched', desc: true })
const sortedRules = computed(() => {
  const rows = [...(rules.data?.rules ?? [])]
  const { by, desc } = sort.value
  rows.sort((a, b) => {
    const x = a[by] ?? ''
    const y = b[by] ?? ''
    const order = typeof x === 'number' && typeof y === 'number' ? x - y : String(x).localeCompare(String(y))
    return desc ? -order : order
  })
  return rows
})
function sortBy(by: RuleSort) {
  sort.value = { by, desc: sort.value.by === by ? !sort.value.desc : by !== 'name' }
}

const number = computed(() => new Intl.NumberFormat(locale.value))
const percent = (part: number, whole: number) =>
  whole === 0
    ? '–'
    : new Intl.NumberFormat(locale.value, { style: 'percent', maximumFractionDigits: 0 }).format(part / whole)
</script>

<template>
  <div class="mx-auto grid max-w-6xl gap-6">
    <div>
      <h2 class="text-2xl font-semibold tracking-tight">{{ $t('stats.title') }}</h2>
      <p class="text-muted-foreground mt-1">{{ $t('stats.subtitle') }}</p>
    </div>

    <!-- One filter row for everything below -->
    <div
      class="bg-background/95 sticky top-14 z-[5] -mx-1 flex flex-wrap items-center gap-4 px-1 py-2 backdrop-blur"
      data-testid="stats-filters"
    >
      <div class="flex items-center gap-2">
        <Label for="stats-range">{{ $t('stats.range') }}</Label>
        <Select v-model="range">
          <SelectTrigger id="stats-range" class="w-44"><SelectValue /></SelectTrigger>
          <SelectContent>
            <SelectItem v-for="preset in RANGE_PRESETS" :key="preset" :value="preset">{{
              $t(`stats.ranges.${preset}`)
            }}</SelectItem>
          </SelectContent>
        </Select>
      </div>
      <div class="flex items-center gap-2">
        <Label for="stats-granularity">{{ $t('stats.granularity') }}</Label>
        <Select v-model="granularity">
          <SelectTrigger id="stats-granularity" class="w-32"><SelectValue /></SelectTrigger>
          <SelectContent>
            <SelectItem v-for="g in ['Day', 'Week', 'Month'] as Granularity[]" :key="g" :value="g">{{
              $t(`stats.granularities.${g}`)
            }}</SelectItem>
          </SelectContent>
        </Select>
      </div>
      <div class="flex items-center gap-2">
        <Label for="stats-account">{{ $t('activity.account') }}</Label>
        <Select
          :model-value="accountId ?? ALL"
          @update:model-value="(v) => (accountId = v === ALL ? undefined : String(v))"
        >
          <SelectTrigger id="stats-account" class="w-52"><SelectValue /></SelectTrigger>
          <SelectContent>
            <SelectItem :value="ALL">{{ $t('activity.allAccounts') }}</SelectItem>
            <SelectItem v-for="account in accounts.accounts" :key="account.id" :value="account.id">{{
              account.name
            }}</SelectItem>
          </SelectContent>
        </Select>
      </div>
      <Button v-if="newData" size="sm" variant="outline" class="ml-auto" data-testid="stats-refresh" @click="loadAll">
        <RefreshCwIcon />
        {{ $t('stats.newData') }}
      </Button>
    </div>

    <!-- Volume -->
    <section class="grid gap-4 lg:grid-cols-2" :aria-label="$t('stats.sections.volume')">
      <ChartCard
        :title="$t('stats.sections.volume')"
        :description="$t('stats.volumeDescription')"
        :loading="volume.loading"
        :loaded="!!volume.data"
        :error="volume.error"
        :empty="!!volume.data && !hasValues(volume.data.totals)"
        :export-href="exportUrl('volume', params)"
        table
      >
        <TimeSeriesChart
          v-if="volume.data"
          :data="volume.data.totals"
          kind="line"
          :granularity="granularity"
          :style-of="keyStyle"
          @select="(b) => drillDown(b)"
        />
        <template #table
          ><SeriesTable v-if="volume.data" :data="volume.data.totals" :granularity="granularity" :style-of="keyStyle"
        /></template>
      </ChartCard>
      <ChartCard
        :title="$t('stats.byActionTitle')"
        :description="$t('stats.byActionDescription')"
        :loading="volume.loading"
        :loaded="!!volume.data"
        :error="volume.error"
        :empty="!!volume.data && volume.data.byAction.series.length === 0"
        table
      >
        <TimeSeriesChart
          v-if="volume.data"
          :data="volume.data.byAction"
          kind="stack"
          :granularity="granularity"
          :style-of="groupStyle"
          @select="(b, key) => drillDown(b, { action: actionOf(key) })"
        />
        <template #table
          ><SeriesTable
            v-if="volume.data"
            :data="volume.data.byAction"
            :granularity="granularity"
            :style-of="groupStyle"
        /></template>
      </ChartCard>
    </section>

    <!-- Rules -->
    <ChartCard
      :title="$t('stats.sections.rules')"
      :description="$t('stats.rulesDescription')"
      :loading="rules.loading"
      :loaded="!!rules.data"
      :error="rules.error"
      :empty="!!rules.data && rules.data.rules.length === 0"
      :export-href="exportUrl('rules', params)"
    >
      <div class="overflow-x-auto">
        <Table data-testid="rule-stats">
          <TableHeader>
            <TableRow>
              <TableHead
                v-for="column in ['name', 'matched', 'actions', 'failed', 'undone', 'lastMatch'] as const"
                :key="column"
                :class="{ 'text-right': column !== 'name' }"
              >
                <button
                  type="button"
                  class="hover:text-foreground inline-flex items-center gap-1"
                  :aria-sort="sort.by === column ? (sort.desc ? 'descending' : 'ascending') : 'none'"
                  @click="sortBy(column)"
                >
                  {{ $t(`stats.ruleColumns.${column}`) }}
                  <span v-if="sort.by === column" aria-hidden="true">{{ sort.desc ? '↓' : '↑' }}</span>
                </button>
              </TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            <TableRow v-for="rule in sortedRules" :key="rule.ruleId">
              <TableCell class="max-w-72">
                <RouterLink
                  :to="{ name: 'rule-edit', params: { id: rule.ruleId } }"
                  class="font-medium hover:underline"
                  >{{ rule.name }}</RouterLink
                >
                <div class="text-muted-foreground flex flex-wrap items-center gap-1 text-xs">
                  {{ rule.accountName }}
                  <Badge v-if="!rule.enabled" variant="outline">{{ $t('rules.disabled') }}</Badge>
                  <Badge v-if="rule.dryRun" variant="outline">{{ $t('rules.dryRun') }}</Badge>
                  <Badge v-if="rule.enabled && rule.matched === 0 && !rule.dryRun" variant="outline">{{
                    $t('stats.neverMatched')
                  }}</Badge>
                </div>
              </TableCell>
              <TableCell class="text-right tabular-nums">{{ number.format(rule.matched) }}</TableCell>
              <TableCell class="text-right tabular-nums">{{ number.format(rule.actions) }}</TableCell>
              <TableCell class="text-right tabular-nums">{{ number.format(rule.failed) }}</TableCell>
              <TableCell class="text-right tabular-nums">{{ number.format(rule.undone) }}</TableCell>
              <TableCell class="text-right whitespace-nowrap">{{
                rule.lastMatch
                  ? new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeZone: 'UTC' }).format(
                      new Date(`${rule.lastMatch}T00:00:00Z`),
                    )
                  : '–'
              }}</TableCell>
            </TableRow>
          </TableBody>
        </Table>
      </div>
    </ChartCard>

    <!-- Senders -->
    <section class="grid gap-4 lg:grid-cols-2" :aria-label="$t('stats.sections.senders')">
      <ChartCard
        :title="$t('stats.topSenders')"
        :description="
          senders.data
            ? $t('stats.sendersSummary', {
                total: number.format(senders.data.totals.processed),
                bulk: percent(senders.data.totals.bulk, senders.data.totals.processed),
              })
            : undefined
        "
        :loading="senders.loading"
        :loaded="!!senders.data"
        :error="senders.error"
        :empty="!!senders.data && senders.data.topProcessed.length === 0"
        :export-href="exportUrl('senders', params)"
      >
        <BarList
          v-if="senders.data"
          :items="
            senders.data.topProcessed.map((s) => ({
              key: s.domain,
              label: s.domain,
              value: s.processed,
              detail: s.bulk ? $t('stats.bulkCount', { count: s.bulk }) : undefined,
            }))
          "
        />
      </ChartCard>
      <ChartCard
        :title="$t('stats.topActioned')"
        :description="$t('stats.topActionedDescription')"
        :loading="senders.loading"
        :loaded="!!senders.data"
        :error="senders.error"
        :empty="!!senders.data && senders.data.topActioned.length === 0"
      >
        <BarList
          v-if="senders.data"
          :items="
            senders.data.topActioned.map((s) => ({
              key: s.domain,
              label: s.domain,
              value: s.actioned,
              detail: $t('stats.ofProcessed', { count: s.processed }),
            }))
          "
        />
      </ChartCard>
    </section>

    <!-- Folders -->
    <section class="grid gap-4 lg:grid-cols-2" :aria-label="$t('stats.sections.folders')">
      <ChartCard
        :title="$t('stats.sections.folders')"
        :description="$t('stats.foldersDescription')"
        :loading="folders.loading"
        :loaded="!!folders.data"
        :error="folders.error"
        :empty="!!folders.data && folders.data.folders.length === 0"
        :export-href="exportUrl('folders', { accountId: params.accountId })"
      >
        <div class="max-h-80 overflow-auto">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{{ $t('stats.folderColumns.folder') }}</TableHead>
                <TableHead class="text-right">{{ $t('stats.folderColumns.messages') }}</TableHead>
                <TableHead class="text-right">{{ $t('stats.folderColumns.unread') }}</TableHead>
                <TableHead class="text-right">{{ $t('stats.folderColumns.change') }}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              <TableRow v-for="folder in folders.data?.folders ?? []" :key="`${folder.accountId}:${folder.folder}`">
                <TableCell class="max-w-64 truncate">
                  {{ folder.folder }}
                  <span v-if="!accountId && accounts.accounts.length > 1" class="text-muted-foreground text-xs">
                    · {{ folder.accountName }}</span
                  >
                </TableCell>
                <TableCell class="text-right tabular-nums">{{ number.format(folder.messages) }}</TableCell>
                <TableCell class="text-right tabular-nums">{{ number.format(folder.unread) }}</TableCell>
                <TableCell class="text-right tabular-nums">{{
                  folder.change === null ? '–' : `${folder.change > 0 ? '+' : ''}${number.format(folder.change)}`
                }}</TableCell>
              </TableRow>
            </TableBody>
          </Table>
        </div>
      </ChartCard>
      <ChartCard
        :title="$t('stats.folderGrowth')"
        :description="$t('stats.folderGrowthDescription')"
        :loading="folders.loading"
        :loaded="!!folders.data"
        :error="folders.error"
        :empty="!!folders.data && !hasValues(mailboxSize)"
        table
      >
        <TimeSeriesChart v-if="mailboxSize" :data="mailboxSize" kind="line" granularity="Day" :style-of="keyStyle" />
        <template #table
          ><SeriesTable v-if="mailboxSize" :data="mailboxSize" granularity="Day" :style-of="keyStyle"
        /></template>
      </ChartCard>
    </section>

    <!-- Security -->
    <section class="grid gap-4" :aria-label="$t('stats.sections.security')">
      <div class="grid grid-cols-2 gap-3 md:grid-cols-5">
        <KpiTile :label="$t('stats.keys.spf')" :value="security.data?.totals.spfFailed ?? null" />
        <KpiTile :label="$t('stats.keys.dkim')" :value="security.data?.totals.dkimFailed ?? null" />
        <KpiTile :label="$t('stats.keys.dmarc')" :value="security.data?.totals.dmarcFailed ?? null" />
        <KpiTile :label="$t('stats.keys.spoofing')" :value="security.data?.totals.spoofing ?? null" />
        <KpiTile :label="$t('stats.keys.highSpam')" :value="security.data?.totals.highSpam ?? null" />
      </div>
      <div class="grid gap-4 lg:grid-cols-2">
        <ChartCard
          :title="$t('stats.authFailures')"
          :description="$t('stats.authFailuresDescription')"
          :loading="security.loading"
          :loaded="!!security.data"
          :error="security.error"
          :empty="!!security.data && !hasValues(authFailures)"
          :export-href="exportUrl('security', params)"
          table
        >
          <TimeSeriesChart
            v-if="authFailures"
            :data="authFailures"
            kind="line"
            :granularity="granularity"
            :style-of="keyStyle"
          />
          <template #table
            ><SeriesTable v-if="authFailures" :data="authFailures" :granularity="granularity" :style-of="keyStyle"
          /></template>
        </ChartCard>
        <ChartCard
          :title="$t('stats.failingDomains')"
          :loading="security.loading"
          :loaded="!!security.data"
          :error="security.error"
          :empty="!!security.data && security.data.topFailingDomains.length === 0"
        >
          <BarList
            v-if="security.data"
            :items="
              security.data.topFailingDomains.map((s) => ({
                key: s.domain,
                label: s.domain,
                value: s.authFailed,
                detail: $t('stats.ofProcessed', { count: s.processed }),
              }))
            "
          />
        </ChartCard>
      </div>
    </section>

    <!-- Reliability -->
    <section class="grid gap-4 lg:grid-cols-2" :aria-label="$t('stats.sections.reliability')">
      <ChartCard
        :title="$t('stats.sections.reliability')"
        :description="
          reliability.data?.pendingNow ? $t('stats.pendingNow', { count: reliability.data.pendingNow }) : undefined
        "
        :loading="reliability.loading"
        :loaded="!!reliability.data"
        :error="reliability.error"
        :empty="!!reliability.data && reliability.data.byTrigger.length === 0"
        :export-href="exportUrl('reliability', params)"
      >
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>{{ $t('stats.triggerColumns.trigger') }}</TableHead>
              <TableHead class="text-right">{{ $t('stats.triggerColumns.runs') }}</TableHead>
              <TableHead class="text-right">{{ $t('stats.triggerColumns.failed') }}</TableHead>
              <TableHead class="text-right">{{ $t('stats.triggerColumns.duration') }}</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            <TableRow v-for="trigger in reliability.data?.byTrigger ?? []" :key="trigger.trigger">
              <TableCell>{{ $t(`activity.triggers.${trigger.trigger}`) }}</TableCell>
              <TableCell class="text-right tabular-nums">{{ number.format(trigger.runs) }}</TableCell>
              <TableCell class="text-right tabular-nums">{{ number.format(trigger.failed) }}</TableCell>
              <TableCell class="text-right tabular-nums">{{
                trigger.averageSeconds === null ? '–' : $t('stats.seconds', { value: trigger.averageSeconds })
              }}</TableCell>
            </TableRow>
          </TableBody>
        </Table>
        <p class="text-muted-foreground mt-3 text-xs">
          <template v-for="(state, i) in reliability.data?.runsByState ?? []" :key="state.state"
            >{{ i > 0 ? ' · ' : '' }}{{ $t(`activity.states.${state.state}`) }}:
            {{ number.format(state.count) }}</template
          >
        </p>
      </ChartCard>
      <ChartCard
        :title="$t('stats.failedActions')"
        :loading="reliability.loading"
        :loaded="!!reliability.data"
        :error="reliability.error"
        :empty="!!reliability.data && !hasValues(failedActions)"
        table
      >
        <TimeSeriesChart
          v-if="failedActions"
          :data="failedActions"
          kind="line"
          :granularity="granularity"
          :style-of="keyStyle"
          @select="(b) => drillDown(b, { failed: 'true' })"
        />
        <template #table
          ><SeriesTable v-if="failedActions" :data="failedActions" :granularity="granularity" :style-of="keyStyle"
        /></template>
      </ChartCard>
    </section>
  </div>
</template>
