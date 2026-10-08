import { useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { ArrowLeft, Ban, Pencil } from 'lucide-react'
import { api, type Schemas } from '../../lib/api/client'
import { ApiError, unwrap } from '../../lib/api/errors'
import { formatDate, formatDateTime, formatTime } from '../../lib/format/date'
import { formatRupees } from '../../lib/format/money'
import { useTransactionDetail } from '../../lib/api/hooks'
import { Badge, Button, Dialog, ErrorBanner } from '../../components/ui'
import { Section, SignedAmount, TxnTypeBadge } from '../../components/ui/money'
import { toast } from '../../lib/toast'
import { NotFoundPage } from '../NotFoundPage'

type History = Schemas['HistoryEntry']

/** History field names (from the API) → the labels used on the entry form. */
const FIELD_LABEL: Record<string, string> = {
  amount: 'txn.amount', date: 'txn.date', time: 'txn.time', category: 'txn.category', account: 'txn.account',
  fromAccount: 'txn.fromAccount', toAccount: 'txn.toAccount', paymentMode: 'txn.paymentMode', adjustmentDirection: 'txn.direction',
  receivedFrom: 'txn.receivedFrom', paidTo: 'txn.paidTo', purpose: 'txn.purpose', referenceNumber: 'txn.reference',
  remarks: 'txn.remarks', status: 'txn.status',
}

/** S07 — one transaction with its accountability trail, edit and cancel (App Flow §4.6, PRD §11). */
export function TransactionDetailPage() {
  const { t } = useTranslation()
  const { id } = useParams()
  const navigate = useNavigate()
  const detail = useTransactionDetail(id)
  const [cancelOpen, setCancelOpen] = useState(false)

  if (detail.isPending) return <p className="text-text-muted">{t('app.loading')}</p>
  // 404 is deliberately indistinguishable from "no access" (App Flow N-6).
  if (detail.error?.status === 404) return <NotFoundPage />
  if (detail.isError) return <ErrorBanner message={detail.error.message} />

  const { transaction: x, canCancel, editableUntil } = detail.data
  // Adjustments are corrected by cancelling and recording a new one (BR-016), so they have no edit form.
  const canEdit = detail.data.canEdit && x.type !== 'ADJUSTMENT'
  const cancelled = x.status === 'CANCELLED'
  const rows: [string, string | undefined | null][] = [
    [t('txn.category'), x.category?.name],
    [t('txn.account'), x.account?.name],
    [t('txn.fromAccount'), x.fromAccount?.name],
    [t('txn.toAccount'), x.toAccount?.name],
    [t('txn.paymentMode'), x.paymentMode?.name],
    [t('txn.direction'), x.adjustmentDirection ? t(`txn.dir.${x.adjustmentDirection}`) : null],
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
        {cancelled && <Badge tone="danger">{t('txn.cancelled')}</Badge>}
        {!cancelled && x.revision > 1 && <Badge tone="warning">{t('txn.edited')}</Badge>}
      </div>

      {cancelled && (
        <div role="note" className="rounded-md border border-danger bg-surface p-3 text-sm">
          <p className="font-medium">{t('txn.cancelledBy', { name: x.cancelledBy?.name ?? '', when: x.cancelledAt ? formatDateTime(x.cancelledAt) : '' })}</p>
          <p>{t('txn.cancelReasonShown', { reason: x.cancellationReason ?? '' })}</p>
          <p className="mt-1 text-text-muted">{t('txn.cancelledNote')}</p>
        </div>
      )}

      <div className="grid justify-items-center gap-2 rounded-lg border border-border bg-surface p-6">
        <TxnTypeBadge type={x.type} />
        <SignedAmount type={x.type} amount={x.amount} adjustment={x.adjustmentDirection ?? null} cancelled={cancelled} className="text-4xl" />
        <p className="text-sm text-text-muted">{formatRupees(x.amount)}</p>
      </div>

      {(canEdit || canCancel) && (
        <div className="flex flex-wrap items-center gap-2">
          {canEdit && (
            <Button variant="secondary" onClick={() => void navigate(`/txn/${x.id}/edit`)}><Pencil aria-hidden className="size-4" />{t('txn.edit')}</Button>
          )}
          {canCancel && (
            <Button variant="secondary" onClick={() => setCancelOpen(true)}><Ban aria-hidden className="size-4" />{t('txn.cancelEntry')}</Button>
          )}
          {canEdit && editableUntil && (
            <p className="text-xs text-text-muted">{t('txn.editableUntil', { when: formatDateTime(editableUntil) })}</p>
          )}
        </div>
      )}

      <Section title={t('txn.details')} id="details-h">
        <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-2 text-sm">
          {rows.filter(([, v]) => v).map(([label, v]) => (
            <div key={label} className="contents"><dt className="text-text-muted">{label}</dt><dd>{v}</dd></div>
          ))}
        </dl>
      </Section>

      <Section title={t('txn.accountability')} id="acct-h">
        <p className="text-sm">{t('txn.recordedBy', { name: x.createdBy.name, when: formatDateTime(x.createdAt) })}</p>
        {x.updatedBy && x.updatedAt && !cancelled && (
          <p className="text-sm">{t('txn.lastEditedBy', { name: x.updatedBy.name, when: formatDateTime(x.updatedAt) })}</p>
        )}
        <p className="text-xs text-text-muted">{t('txn.source', { source: x.source })}</p>
      </Section>

      <HistorySection id={x.id} revision={x.revision} />

      <CancelDialog open={cancelOpen} onClose={() => setCancelOpen(false)} txn={x} />
    </div>
  )
}

function HistorySection({ id, revision }: { id: string; revision: number }) {
  const { t } = useTranslation()
  // Keyed by revision, so an edit or cancel always shows its new history entry.
  const history = useQuery<History[], ApiError>({
    queryKey: ['transaction', id, 'history', revision],
    queryFn: async () => unwrap(await api.GET('/api/v1/transactions/{id}/history', { params: { path: { id } } })),
  })

  return (
    <Section title={t('txn.history')} id="history-h">
      {history.isPending && <p className="text-sm text-text-muted">{t('app.loading')}</p>}
      {history.isError && <ErrorBanner message={history.error.message} />}
      {history.data && (
        <ol className="grid gap-3">
          {history.data.map((h) => (
            <li key={h.revision} className="grid gap-1 border-l-2 border-border pl-3 text-sm">
              <p>
                <span className="font-medium">{t(`txn.historyKind.${h.kind}`)}</span>
                {' · '}{h.changedBy.name}{' · '}<span className="text-text-muted">{formatDateTime(h.changedAt)}</span>
              </p>
              {h.reason && <p className="text-text-muted">{t('txn.historyReason', { reason: h.reason })}</p>}
              {h.changes.filter((c) => c.field !== 'status').length > 0 && (
                <ul className="grid gap-0.5">
                  {h.changes.filter((c) => c.field !== 'status').map((c) => (
                    <li key={c.field}>
                      <span className="text-text-muted">{t(FIELD_LABEL[c.field] ?? c.field)}: </span>
                      <del className="text-text-muted">{show(c.field, c.old) || '—'}</del>{' → '}<ins className="no-underline font-medium">{show(c.field, c.new) || '—'}</ins>
                    </li>
                  ))}
                </ul>
              )}
            </li>
          ))}
        </ol>
      )}
    </Section>
  )
}

function show(field: string, value: string | null): string {
  if (value === null) return ''
  if (field === 'amount') return formatRupees(value)
  if (field === 'date') return formatDate(value)
  if (field === 'time') return formatTime(value)
  return value
}

function CancelDialog({ open, onClose, txn }: { open: boolean; onClose: () => void; txn: Schemas['TransactionDto'] }) {
  const { t } = useTranslation()
  const queryClient = useQueryClient()
  const [reason, setReason] = useState('')
  const [error, setError] = useState<string | null>(null)
  const tooShort = reason.trim().length < 5

  const cancel = useMutation({
    mutationFn: async () => unwrap(await api.POST('/api/v1/transactions/{id}/cancel', {
      params: { path: { id: txn.id }, header: { 'If-Match': `"${txn.revision}"` } },
      body: { reason: reason.trim() },
    })),
    onSuccess: async (result) => {
      toast.success(t('txn.cancelledToast', { number: result.transaction.txnNumber }))
      close()
      await Promise.all(['dashboard', 'transactions', 'account-balances', 'transaction'].map((k) => queryClient.invalidateQueries({ queryKey: [k] })))
    },
    onError: (err) => setError(err instanceof ApiError ? err.message : t('errors.generic')),
  })

  function close() {
    setReason('')
    setError(null)
    onClose()
  }

  return (
    <Dialog open={open} onClose={close} title={t('txn.cancelTitle', { number: txn.txnNumber })}
      footer={<>
        <Button variant="secondary" onClick={close}>{t('txn.keepEntry')}</Button>
        <Button variant="destructive" loading={cancel.isPending} disabled={tooShort} onClick={() => cancel.mutate()}>{t('txn.cancelEntry')}</Button>
      </>}>
      <ErrorBanner message={error} />
      <p className="text-sm">{t('txn.cancelBody', { amount: formatRupees(txn.amount) })}</p>
      <label className="grid gap-1 text-sm font-medium">
        {t('txn.cancelReason')}
        <textarea value={reason} onChange={(e) => setReason(e.target.value)} rows={3} maxLength={500} required
          className="rounded-md border border-border bg-surface p-2 font-normal" />
        <span className="text-xs font-normal text-text-muted">{t('txn.cancelReasonHint')}</span>
      </label>
    </Dialog>
  )
}
