import { Link } from 'react-router'
import { useTranslation } from 'react-i18next'
import type { Txn } from '../lib/api/hooks'
import { formatTime } from '../lib/format/date'
import { Badge } from './ui'
import { SignedAmount, TxnTypeBadge } from './ui/money'

/** One ledger row (Design Brief §5): type, signed amount, what it was for, and WHO recorded it. */
export function TxnRow({ txn }: { txn: Txn }) {
  const { t } = useTranslation()
  const cancelled = txn.status === 'CANCELLED'
  const label = txn.type === 'TRANSFER' ? `${txn.fromAccount?.name ?? ''} → ${txn.toAccount?.name ?? ''}` : txn.category?.name ?? txn.account?.name ?? ''
  return (
    <Link to={`/txn/${txn.id}`} className={`flex min-h-16 items-center gap-3 rounded-md border border-border bg-surface px-3 py-2 hover:bg-surface-muted ${cancelled ? 'opacity-60' : ''}`}>
      <div className="grid min-w-0 flex-1 gap-0.5">
        <div className="flex flex-wrap items-center gap-2">
          <TxnTypeBadge type={txn.type} />
          <span className="truncate font-medium">{label}</span>
          {cancelled && <Badge tone="danger">{t('txn.cancelled')}</Badge>}
          {txn.revision > 1 && !cancelled && <Badge>{t('txn.edited')}</Badge>}
        </div>
        <p className="truncate text-sm text-text-muted">{txn.purpose}</p>
        <p className="text-xs text-text-muted">{txn.createdBy.name} · {formatTime(txn.txnTime)} · {txn.txnNumber}</p>
      </div>
      <SignedAmount type={txn.type} amount={txn.amount} adjustment={txn.adjustmentDirection ?? null} cancelled={cancelled} className="text-lg" />
    </Link>
  )
}
