import { useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { ArrowLeft } from 'lucide-react'
import { api } from '../../lib/api/client'
import { ApiError, unwrap, type FieldErrors } from '../../lib/api/errors'
import { useAccountBalances, useAccounts } from '../../lib/api/hooks'
import { useMe, useSelectedFund } from '../../lib/auth/queries'
import { formatRupees, toWireAmount } from '../../lib/format/money'
import { nowTime, todayIso } from '../../lib/format/date'
import { Button, Dialog, ErrorBanner, Field } from '../../components/ui'
import { AmountInput, ChipGroup } from '../../components/ui/money'
import { toast } from '../../lib/toast'

type Direction = 'INCREASE' | 'DECREASE'

/**
 * Adjustment (Admin only, BR-016): corrects one account's balance after a count or reconciliation,
 * in either direction, with a mandatory reason. It is an entry like any other, so it shows in the
 * ledger, the history and the audit log.
 */
export function AdjustmentFormPage() {
  const { t } = useTranslation()
  const me = useMe()
  const fund = useSelectedFund(me.data)
  if (me.isPending) return <p className="text-text-muted">{t('app.loading')}</p>
  if (!fund) return <p className="py-16 text-center">{t('txn.noFund')}</p>
  if (fund.status !== 'ACTIVE') return <p className="py-16 text-center">{t('txn.closedFund', { fund: fund.name })}</p>
  return <AdjustmentForm key={fund.id} fund={fund} />
}

function AdjustmentForm({ fund }: { fund: { id: string; name: string } }) {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const accounts = useAccounts()
  const balances = useAccountBalances(fund.id)
  const [amount, setAmount] = useState('')
  const [accountId, setAccountId] = useState<string | undefined>()
  const [direction, setDirection] = useState<Direction>('DECREASE')
  const [date, setDate] = useState(() => todayIso())
  const [reason, setReason] = useState('')
  const [remarks, setRemarks] = useState('')
  const [errors, setErrors] = useState<FieldErrors>({})
  const [banner, setBanner] = useState<string | null>(null)
  const [confirm, setConfirm] = useState(false)
  const [clientTxnId] = useState(() => crypto.randomUUID())

  const account = accountId ?? accounts.data?.[0]?.id ?? ''
  const accountName = accounts.data?.find((a) => a.id === account)?.name ?? ''
  const balance = balances.data?.find((b) => b.accountId === account)?.closing
  const wire = toWireAmount(amount)

  const save = useMutation({
    mutationFn: async () => unwrap(await api.POST('/api/v1/transactions/adjustment', {
      body: {
        fundId: fund.id, amount: wire!, txnDate: date, txnTime: date === todayIso() ? nowTime() : '00:00', accountId: account, direction,
        reason: reason.trim(), remarks: remarks.trim() || null, clientTxnId,
      },
    })),
    onSuccess: async (result) => {
      toast.success(t('txn.savedToast', { number: result.transaction.txnNumber }))
      await Promise.all(['dashboard', 'transactions', 'account-balances'].map((k) => queryClient.invalidateQueries({ queryKey: [k] })))
      void navigate(`/txn/${result.transaction.id}`, { replace: true })
    },
    onError: (err) => {
      setConfirm(false)
      if (err instanceof ApiError) {
        setErrors(err.fieldErrors)
        setBanner(err.message)
      } else {
        setBanner(t('errors.generic'))
      }
    },
  })

  function review() {
    const next: FieldErrors = {}
    if (!wire) next.amount = [t('txn.errAmount')]
    if (!account) next.accountId = [t('txn.errAccount')]
    if (reason.trim().length < 10) next.reason = [t('adjust.errReason')]
    setErrors(next)
    setBanner(null)
    if (Object.keys(next).length === 0) setConfirm(true)
  }

  const err = (f: string) => errors[f]?.[0]
  return (
    <div className="mx-auto grid max-w-xl gap-5">
      <div className="flex items-center gap-2">
        <Link to="/" aria-label={t('actions.close')} className="flex size-11 items-center justify-center rounded-md hover:bg-surface-muted"><ArrowLeft aria-hidden className="size-5" /></Link>
        <div>
          <h1 className="text-xl font-semibold">{t('screens.newAdjustment')}</h1>
          <p className="text-sm text-text-muted">{fund.name}</p>
        </div>
      </div>
      <p className="text-sm text-text-muted">{t('adjust.intro')}</p>

      <form onSubmit={(e) => { e.preventDefault(); review() }} className="grid gap-5" noValidate>
        <ErrorBanner message={banner} />
        <ChipGroup label={t('txn.account')} required moreLabel={t('txn.more')} value={account} onChange={setAccountId} error={err('accountId')}
          options={(accounts.data ?? []).map((a) => {
            const closing = balances.data?.find((b) => b.accountId === a.id)?.closing
            return { id: a.id, label: a.name, hint: closing !== undefined ? formatRupees(closing, { decimals: 'auto' }) : undefined }
          })} />
        <ChipGroup label={t('txn.direction')} required moreLabel={t('txn.more')} value={direction} onChange={(v) => setDirection(v as Direction)}
          options={[{ id: 'DECREASE', label: t('txn.dir.DECREASE') }, { id: 'INCREASE', label: t('txn.dir.INCREASE') }]} />
        <AmountInput label={t('txn.amount')} value={amount} onChange={setAmount} error={err('amount')} accent="adjust" />
        <Field label={t('adjust.reason')} required value={reason} onChange={(e) => setReason(e.target.value)} error={err('reason')} maxLength={500} hint={t('adjust.reasonHint')} />
        <Field label={t('txn.date')} type="date" required value={date} max={todayIso()} onChange={(e) => setDate(e.target.value)} error={err('txnDate')} />
        <Field label={t('txn.remarks')} value={remarks} onChange={(e) => setRemarks(e.target.value)} />
        <Button type="submit" size="lg">{t('txn.review')}</Button>
      </form>

      <Dialog open={confirm} onClose={() => setConfirm(false)} title={t('txn.confirmTitle', { type: t('txn.type.ADJUSTMENT') })}
        footer={<>
          <Button variant="secondary" onClick={() => setConfirm(false)}>{t('txn.edit')}</Button>
          <Button loading={save.isPending} onClick={() => save.mutate()}>{t('adjust.save')}</Button>
        </>}>
        <p className="amount text-center text-3xl font-bold">{direction === 'INCREASE' ? '+ ' : '− '}{wire ? formatRupees(wire) : ''}</p>
        <p className="text-sm">{t('adjust.confirmBody', { account: accountName, balance: balance !== undefined ? formatRupees(balance) : '—' })}</p>
        <p className="text-sm text-text-muted">{reason.trim()}</p>
      </Dialog>
    </div>
  )
}
