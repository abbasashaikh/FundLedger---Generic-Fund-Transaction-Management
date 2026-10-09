import { useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { useTranslation } from 'react-i18next'
import { ArrowLeft, RefreshCw } from 'lucide-react'
import { useSession } from '../../lib/auth/session'
import { formatDateTime } from '../../lib/format/date'
import { formatRupees } from '../../lib/format/money'
import type { OutboxItem } from '../../lib/offline/db'
import { discard, entryTitle, useOutbox } from '../../lib/offline/outbox'
import { syncNow, useSyncStatus } from '../../lib/offline/sync'
import { useOnline } from '../../pwa/useOnline'
import { Badge, Button, Dialog } from '../../components/ui'
import { TxnTypeBadge } from '../../components/ui/money'

const KIND = { DEPOSIT: 'in', EXPENSE: 'out', TRANSFER: 'transfer' } as const

/** S13 — what is waiting on this device, what needs attention, and a way to send it now (App Flow §4.8). */
export function SyncPage() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const userId = useSession((s) => s.user?.id)
  const online = useOnline()
  const syncing = useSyncStatus((s) => s.syncing)
  const items = useOutbox(userId)
  const [confirm, setConfirm] = useState<OutboxItem | null>(null)

  const attention = items.filter((i) => i.state === 'NEEDS_ATTENTION')
  const waiting = items.filter((i) => i.state !== 'NEEDS_ATTENTION')

  return (
    <div className="mx-auto grid max-w-2xl gap-4">
      <div className="flex flex-wrap items-center gap-2">
        <Link to="/" aria-label={t('actions.close')} className="flex size-11 items-center justify-center rounded-md hover:bg-surface-muted"><ArrowLeft aria-hidden className="size-5" /></Link>
        <h1 className="text-xl font-semibold">{t('sync.title')}</h1>
        <Button variant="secondary" className="ml-auto" loading={syncing} disabled={!online || waiting.length === 0} onClick={() => void syncNow({ force: true })}>
          <RefreshCw aria-hidden className="size-4" />{t('sync.syncNow')}
        </Button>
      </div>

      {!online && <p role="status" className="rounded-md border border-border bg-surface-muted px-3 py-2 text-sm">{t('pwa.offline')}</p>}
      {items.length === 0 && <p className="py-12 text-center text-text-muted">{t('sync.empty')}</p>}

      {attention.length > 0 && (
        <section aria-labelledby="attention-h" className="grid gap-2">
          <h2 id="attention-h" className="font-semibold text-danger">{t('sync.attentionHeading')}</h2>
          {attention.map((i) => (
            <article key={i.clientTxnId} className="grid gap-2 rounded-lg border border-danger bg-surface p-3">
              <Row item={i} />
              <p role="alert" className="text-sm">{i.lastError?.message}</p>
              <div className="flex flex-wrap gap-2">
                <Button onClick={() => void navigate(`/new/${KIND[i.command.type as keyof typeof KIND]}?resume=${i.clientTxnId}`)}>{t('sync.editRetry')}</Button>
                <Button variant="secondary" onClick={() => setConfirm(i)}>{t('sync.discard')}</Button>
              </div>
            </article>
          ))}
        </section>
      )}

      {waiting.length > 0 && (
        <section aria-labelledby="waiting-h" className="grid gap-2">
          <h2 id="waiting-h" className="font-semibold">{t('sync.waitingHeading')}</h2>
          {waiting.map((i) => (
            <article key={i.clientTxnId} className="grid gap-1 rounded-lg border border-border bg-surface p-3">
              <Row item={i} />
              {i.attempts > 0 && i.lastError && <p className="text-xs text-text-muted">{i.lastError.message}</p>}
            </article>
          ))}
        </section>
      )}

      <Dialog open={!!confirm} onClose={() => setConfirm(null)} title={t('sync.discardTitle')}
        footer={<>
          <Button variant="secondary" onClick={() => setConfirm(null)}>{t('actions.cancel')}</Button>
          <Button variant="destructive" onClick={() => {
            const target = confirm
            setConfirm(null)
            if (target) void discard(target, `${t(`txn.type.${target.command.type}`)} ₹${target.command.amount} · ${entryTitle(target)}`).then(() => syncNow())
          }}>{t('sync.discard')}</Button>
        </>}>
        <p>{t('sync.discardBody')}</p>
        {confirm && <p className="amount font-semibold">{t(`txn.type.${confirm.command.type}`)} {formatRupees(confirm.command.amount)}</p>}
      </Dialog>
    </div>
  )
}

function Row({ item }: { item: OutboxItem }) {
  const { t } = useTranslation()
  return (
    <div className="flex flex-wrap items-center gap-2">
      <TxnTypeBadge type={item.command.type} />
      <span className="amount font-semibold">{formatRupees(item.command.amount)}</span>
      <span className="min-w-0 flex-1 truncate text-sm text-text-muted">{entryTitle(item)} · {item.command.purpose}</span>
      <Badge tone={item.state === 'NEEDS_ATTENTION' ? 'danger' : 'warning'}>{t(`sync.state.${item.state}`)}</Badge>
      <span className="w-full text-xs text-text-muted">{item.display.fundName} · {t('sync.savedOn', { when: formatDateTime(item.createdAt) })}</span>
    </div>
  )
}
