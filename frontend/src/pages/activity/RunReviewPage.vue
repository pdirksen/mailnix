<script setup lang="ts">
import { ApiError } from '@/api/client'
import RunStateBadge from '@/components/runs/RunStateBadge.vue'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { useConnectionErrors } from '@/lib/connectionErrors'
import { formatDateTime, formatRelative } from '@/lib/format'
import { isTerminal } from '@/rules/model'
import { describeAction } from '@/rules/describe'
import { type RunDetail, useRunsStore } from '@/stores/runs'
import { ArrowLeftIcon, Loader2Icon, ShieldAlertIcon } from '@lucide/vue'
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterLink, useRouter } from 'vue-router'
import { toast } from 'vue-sonner'

const props = defineProps<{ id: string }>()

const { t, locale } = useI18n()
const router = useRouter()
const runs = useRunsStore()
const { describeRequestError } = useConnectionErrors()

const detail = ref<RunDetail | null>(null)
const loading = ref(true)
const notFound = ref(false)
const busy = ref<'confirm' | 'cancel' | null>(null)

async function load() {
  try {
    detail.value = await runs.fetchDetail(props.id)
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) notFound.value = true
    else toast.error(describeRequestError(error).message)
  } finally {
    loading.value = false
  }
}

onMounted(load)
// The run may expire or be handled elsewhere while this page is open.
watch(
  () => runs.runs.find((r) => r.id === props.id)?.state,
  (state, previous) => {
    if (previous && state !== previous) void load()
  },
)

const waiting = computed(() => detail.value?.run.state === 'AwaitingConfirmation')
const moving = computed(
  () => detail.value?.pending.filter((m) => m.actions.some((a) => !a.dryRun && isTerminal(a))).length ?? 0,
)

async function confirm() {
  busy.value = 'confirm'
  try {
    await runs.confirm(props.id)
    toast.success(t('activity.confirmed'))
    await router.push({ name: 'activity', query: { run: props.id } })
  } catch (error) {
    toast.error(describeRequestError(error).message)
    await load()
  } finally {
    busy.value = null
  }
}

async function cancel() {
  busy.value = 'cancel'
  try {
    await runs.cancel(props.id)
    toast.success(t('activity.cancelled'))
    await router.push({ name: 'activity' })
  } catch (error) {
    toast.error(describeRequestError(error).message)
    await load()
  } finally {
    busy.value = null
  }
}
</script>

<template>
  <div class="mx-auto grid max-w-5xl gap-6">
    <RouterLink :to="{ name: 'activity' }" class="text-muted-foreground hover:text-foreground flex w-fit items-center gap-1 text-sm">
      <ArrowLeftIcon class="size-4" />
      {{ $t('activity.title') }}
    </RouterLink>

    <p v-if="notFound" class="text-muted-foreground">{{ $t('activity.notWaiting') }}</p>
    <Skeleton v-else-if="loading || !detail" class="h-64" />

    <template v-else>
      <div class="flex flex-wrap items-center justify-between gap-4">
        <div class="flex flex-wrap items-center gap-3">
          <h2 class="text-2xl font-semibold tracking-tight">{{ $t('activity.reviewTitle') }}</h2>
          <RunStateBadge :state="detail.run.state" :undone="!!detail.run.undoneByRunId" />
        </div>
        <div v-if="waiting" class="flex gap-2">
          <Button variant="outline" :disabled="busy !== null" data-testid="cancel-run" @click="cancel">
            <Loader2Icon v-if="busy === 'cancel'" class="animate-spin" />
            {{ $t('activity.cancelRun') }}
          </Button>
          <Button :disabled="busy !== null" data-testid="confirm-run" @click="confirm">
            <Loader2Icon v-if="busy === 'confirm'" class="animate-spin" />
            {{ $t('activity.confirm') }}
          </Button>
        </div>
      </div>

      <p class="text-muted-foreground text-sm">
        {{ detail.run.accountName }} · {{ detail.run.folder }} · {{ $t(`activity.triggers.${detail.run.trigger}`) }} ·
        <span :title="formatDateTime(detail.run.createdAt, locale)">{{ formatRelative(detail.run.createdAt, locale) }}</span>
        <template v-if="waiting && detail.run.expiresAt"> · {{ $t('activity.expiresAt', { time: formatRelative(detail.run.expiresAt, locale) }) }}</template>
      </p>

      <Alert v-if="waiting" class="border-warning/50">
        <ShieldAlertIcon class="text-warning" />
        <AlertTitle>{{ $t('activity.messagesAffected', { count: detail.pending.length }) }}</AlertTitle>
        <AlertDescription>{{ $t('activity.reviewText', { count: moving }) }}</AlertDescription>
      </Alert>
      <Alert v-else>
        <AlertDescription>
          {{ $t('activity.notWaiting') }}
          <RouterLink :to="{ name: 'activity', query: { run: detail.run.id } }" class="underline">{{ $t('runNow.show') }}</RouterLink>
        </AlertDescription>
      </Alert>

      <div v-if="waiting" class="overflow-x-auto rounded-lg border">
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>{{ $t('activity.message') }}</TableHead>
              <TableHead>{{ $t('activity.action') }}</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            <TableRow v-for="message in detail.pending" :key="message.uid" data-testid="pending-message">
              <TableCell class="max-w-96">
                <p class="truncate text-sm font-medium">{{ message.subject || '—' }}</p>
                <p class="text-muted-foreground truncate text-xs">{{ message.from }}</p>
              </TableCell>
              <TableCell>
                <div class="flex flex-wrap gap-1">
                  <Badge
                    v-for="(action, i) in message.actions"
                    :key="i"
                    :variant="action.dryRun ? 'outline' : 'secondary'"
                    class="max-w-full truncate"
                    :title="action.ruleName"
                  >
                    {{ describeAction(action, t) }}<template v-if="action.dryRun"> ({{ $t('activity.dryRun') }})</template>
                  </Badge>
                </div>
              </TableCell>
            </TableRow>
          </TableBody>
        </Table>
      </div>
    </template>
  </div>
</template>
