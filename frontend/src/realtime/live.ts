import { i18n } from '@/i18n'
import { router } from '@/router'
import { useAccountsStore } from '@/stores/accounts'
import { useRunsStore } from '@/stores/runs'
import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr'
import { useDebounceFn } from '@vueuse/core'
import { toast } from 'vue-sonner'

interface RunEvent {
  runId: string
  accountId: string
  state: string
  trigger: string
}

interface AccountStatusEvent {
  accountId: string
}

let connection: HubConnection | null = null

/** Opens the live connection (account status, run progress). Safe to call repeatedly. */
export async function connectLive() {
  if (connection && connection.state !== HubConnectionState.Disconnected) return
  const runs = useRunsStore()
  const accounts = useAccountsStore()
  const t = i18n.global.t as (key: string, named?: Record<string, unknown>) => string

  // Several events arrive in quick succession during a run; one refresh is enough.
  const refreshRuns = useDebounceFn(() => runs.fetchRecent().catch(() => {}), 300)

  connection = new HubConnectionBuilder()
    .withUrl('/hubs/live')
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build()

  connection.on('runUpdated', (event: RunEvent) => {
    void refreshRuns()
    if (event.state === 'AwaitingConfirmation') {
      toast.warning(t('activity.awaitingToast'), {
        action: { label: t('activity.review'), onClick: () => void router.push({ name: 'run-review', params: { id: event.runId } }) },
      })
    }
  })
  connection.on('accountStatusChanged', (event: AccountStatusEvent) => {
    void accounts.fetchOne(event.accountId).catch(() => {})
  })
  connection.onreconnected(() => void refreshRuns())

  try {
    await connection.start()
  } catch {
    // The app works without live updates; automatic reconnect only applies after a first successful start.
    connection = null
  }
}

export async function disconnectLive() {
  const current = connection
  connection = null
  await current?.stop()
}
