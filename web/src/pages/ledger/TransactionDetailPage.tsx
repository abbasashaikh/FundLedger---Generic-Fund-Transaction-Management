import { Link, useParams } from 'react-router'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { ArrowLeft } from 'lucide-react'
import { api, type Schemas } from '../../lib/api/client'
import { ApiError, unwrap } from '../../lib/api/errors'
import { formatDate, formatDateTime, formatTime } from '../../lib/format/date'
import { formatRupees } from '../../lib/format/money'
import { Badge, ErrorBanner } from '../../components/ui'
import { Section, SignedAmount, TxnTypeBadge } from '../../components/ui/money'
import { NotFoundPage } from '../NotFoundPage'

/** S07 — one transaction with its accountability trail (App Flow §4.6). Edit/cancel arrive in Phase 3. */
export function TransactionDetailPage() {
  const { t } = useTranslation()
  const { id } = useParams()
  const txn = useQuery<Schemas['TransactionDto'], ApiError>({
    queryKey: ['transaction', id],
    queryFn: async () => unwrap(await api.GET('/api/v1/transactions/{id}', { params: { path: { id: id! } } })),
  })

  if (txn.isPending) return <p className="text-text-muted">{t('app.loading')}</p>
  // 404 is deliberately indistinguishable from "no access" (App Flow N-6).
  if (txn.error?.status === 404) return <NotFoundPage />
  if (txn.isError) return <ErrorBanner message={txn.error.message} />

  const x = txn.data
  const rows: [string, string | undefined | null][] = [
    [t('txn.fund'), undefined],
    [t('txn.category'), x.category?.name],
    [t('txn.account'), x.account?.name],
    [t('txn.fromAccount'), x.fromAccount?.name],
    [t('txn.toAccount'), x.toAccount?.name],
    [t('txn.paymentMode'), x.paymentMode?.name],
    [t('txn.receivedFrom'), x.receivedFrom],
    [t('txn.paidTo'), x.paidTo],
    [t('txn.purpose'), x.purpose],
    [t('txn.reference'), x.referenceNumber],
    [t('txn.remarks'), x.remarks],
    [t('txn.when'), `${formatDate(x.txnDate)} ${formatTime(x.txnTime)}`],
  ]

  return (
    <div className="mx-auto grid max-w-2xl gap-4">
      <div className="flex items-center gap-2">
        <Link to="/ledger" aria-label={t('screens.ledger')} className="flex size-11 items-center justify-center rounded-md hover:bg-surface-muted"><ArrowLeft aria-hidden className="size-5" /></Link>
        <h1 className="text-xl font-semibold">{x.txnNumber}</h1>
        {x.status === 'CANCELLED' && <Badge tone="danger">{t('txn.cancelled')}</Badge>}
      </div>

      <div className="grid justify-items-center gap-2 rounded-lg border border-border bg-surface p-6">
        <TxnTypeBadge type={x.type} />
        <SignedAmount type={x.type} amount={x.amount} adjustment={x.adjustmentDirection ?? null} cancelled={x.status === 'CANCELLED'} className="text-4xl" />
        <p className="text-sm text-text-muted">{formatRupees(x.amount)}</p>
      </div>

      <Section title={t('txn.details')} id="details-h">
        <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-2 text-sm">
          {rows.filter(([, v]) => v).map(([label, v]) => (
            <div key={label} className="contents"><dt className="text-text-muted">{label}</dt><dd>{v}</dd></div>
          ))}
        </dl>
      </Section>

      <Section title={t('txn.accountability')} id="acct-h">
        <p className="text-sm">{t('txn.recordedBy', { name: x.createdBy.name, when: formatDateTime(x.createdAt) })}</p>
        <p className="text-xs text-text-muted">{t('txn.source', { source: x.source })}</p>
      </Section>
    </div>
  )
}
