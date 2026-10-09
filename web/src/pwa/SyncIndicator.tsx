import { useEffect } from 'react'
import { Link } from 'react-router'
import { useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { CheckCircle2, CloudOff, RefreshCw, TriangleAlert } from 'lucide-react'
import { useSession } from '../lib/auth/session'
import { countOutbox, useOutbox } from '../lib/offline/outbox'
import { syncNow, useSyncStatus } from '../lib/offline/sync'
import { toast } from '../lib/toast'
import { useOnline } from './useOnline'

const SYNC_TICK_MS = 60_000

/**
 * Starts the sync engine at the right moments (TRD §8.2): when the app opens, when the connection returns, and every
 * minute while something is waiting. The engine itself decides what is due. After a run it refreshes what the screens show.
 */
export function SyncController() {
  const { t } = useTranslation()
  const queryClient = useQueryClient()
  const userId = useSession((s) => s.user?.id)
  const online = useOnline()
  const items = useOutbox(userId)
  const waiting = items.some((i) => i.state === 'PENDING' || i.state === 'SYNCING')
  const reportId = useSyncStatus((s) => s.reportId)

  useEffect(() => {
    if (userId && online) void syncNow()
  }, [userId, online])

  useEffect(() => {
    if (!userId || !online || !waiting) return
    const timer = setInterval(() => void syncNow(), SYNC_TICK_MS)
    return () => clearInterval(timer)
  }, [userId, online, waiting])

  useEffect(() => {
    const report = useSyncStatus.getState().lastReport
    if (!report) return
    if (report.synced > 0) {
      toast.success(t('sync.syncedToast', { count: report.synced }))
      void Promise.all(['dashboard', 'transactions', 'account-balances'].map((k) => queryClient.invalidateQueries({ queryKey: [k] })))
    }

    if (report.rejected > 0) toast.error(t('sync.rejectedToast', { count: report.rejected }))
  }, [reportId, queryClient, t])

  return null
}

/** Top-bar status (App Flow §4.8): offline cloud, ⚠ needs attention, ⟳ with a count, or ✓ everything synced. Opens the sync screen. */
export function SyncIndicator() {
  const { t } = useTranslation()
  const userId = useSession((s) => s.user?.id)
  const online = useOnline()
  const syncing = useSyncStatus((s) => s.syncing)
  const counts = countOutbox(useOutbox(userId))

  let icon = <CheckCircle2 aria-hidden className="size-5 text-success" />
  let label = t('sync.allSynced')
  if (counts.needsAttention > 0) {
    icon = <TriangleAlert aria-hidden className="size-5 text-danger" />
    label = t('sync.needsAttention', { count: counts.needsAttention })
  } else if (counts.pending > 0) {
    icon = <RefreshCw aria-hidden className={`size-5 text-primary ${syncing ? 'animate-spin' : ''}`} />
    label = t('sync.waiting', { count: counts.pending })
  } else if (!online) {
    icon = <CloudOff aria-hidden className="size-5 text-text-muted" />
    label = t('sync.offline')
  }

  return (
    <Link to="/sync" title={label} className="ml-auto flex min-h-11 items-center gap-1 rounded-md px-1 text-sm text-text-muted hover:bg-surface-muted lg:ml-2">
      {icon}
      {counts.total > 0 && <span className="amount font-medium text-text">{counts.total}</span>}
      <span className="sr-only">{label}</span>
    </Link>
  )
}
