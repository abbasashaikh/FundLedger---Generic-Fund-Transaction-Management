import { useMemo, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { ArrowLeft, ArrowUpDown, TriangleAlert } from 'lucide-react'
import { api, type Schemas } from '../../lib/api/client'
import { ApiError, unwrap, type FieldErrors } from '../../lib/api/errors'
import { useAccountBalances, useAccounts, useCategories, usePaymentModes } from '../../lib/api/hooks'
import { useMe, useSelectedFund } from '../../lib/auth/queries'
import { compareAmounts, formatRupees, toWireAmount } from '../../lib/format/money'
import { formatDate, formatTime, nowTime, todayIso } from '../../lib/format/date'
import { Button, Dialog, ErrorBanner, Field } from '../../components/ui'
import { AmountInput, ChipGroup, TxnTypeBadge } from '../../components/ui/money'
import { toast } from '../../lib/toast'

export type EntryKind = 'in' | 'out' | 'transfer'

type Result = Schemas['TransactionResult']
type Detail = Schemas['TransactionDetail']
type Remembered = { categoryId?: string; accountId?: string; modeId?: string; fromId?: string; toId?: string }

const memKey = (kind: EntryKind, fundId: string) => `fl.last.${kind}.${fundId}`
const recall = (kind: EntryKind, fundId: string): Remembered => {
  try { return JSON.parse(localStorage.getItem(memKey(kind, fundId)) ?? '{}') as Remembered } catch { return {} }
}

const META = {
  in: { type: 'DEPOSIT', accent: 'in', title: 'txn.titleIn', save: 'txn.saveIn', direction: 'MONEY_IN', allow: 'moneyIn' },
  out: { type: 'EXPENSE', accent: 'out', title: 'txn.titleOut', save: 'txn.saveOut', direction: 'MONEY_OUT', allow: 'moneyOut' },
  transfer: { type: 'TRANSFER', accent: 'transfer', title: 'txn.titleTransfer', save: 'txn.saveTransfer', direction: undefined, allow: 'transfer' },
} as const

/**
 * S08/S09/S10 — Money In, Money Out and Transfer (App Flow §4.2–4.3). Amount first; chips for
 * category/account/mode with the user's last choices remembered per fund; a confirmation step
 * before saving; and one client id per entry, so a retry or double-tap can never record twice.
 */
export function TransactionFormPage({ kind }: { kind: EntryKind }) {
  const meta = META[kind]
  const { t } = useTranslation()
  const me = useMe()
  const fund = useSelectedFund(me.data)

  if (me.isPending) return <p className="text-text-muted">{t('app.loading')}</p>
  if (!fund) return <Notice text={t('txn.noFund')} />
  if (!fund.permissions[meta.allow]) return <Notice text={t('txn.noPermission')} />
  if (fund.status !== 'ACTIVE') return <Notice text={t('txn.closedFund', { fund: fund.name })} />
  // Keyed by fund so switching funds can never carry one fund's choices into another.
  return <EntryForm key={`${kind}:${fund.id}`} kind={kind} fund={fund} />
}

function Notice({ text }: { text: string }) {
  const { t } = useTranslation()
  return (
    <div className="mx-auto max-w-xl py-16 text-center">
      <p>{text}</p>
      <Link to="/" className="mt-4 inline-flex min-h-11 items-center rounded-md bg-primary px-4 font-medium text-primary-fg">{t('notFound.home')}</Link>
    </div>
  )
}

/** Active options, plus the one an entry being edited already uses (it may have been turned off since). */
function usable<T extends { id: string; isActive: boolean }>(list: T[] | undefined, keep: (string | undefined)[]): T[] | undefined {
  return list?.filter((o) => o.isActive || keep.includes(o.id))
}

/**
 * The entry form. With `edit`, it edits an existing entry (S07 → Edit, App Flow §4.6): fields start from
 * the saved values, the save sends If-Match with the revision that was loaded, and an Admin must give a reason.
 */
export function EntryForm({ kind, fund, edit }: { kind: EntryKind; fund: { id: string; name: string }; edit?: Detail }) {
  const meta = META[kind]
  const { t } = useTranslation()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const x = edit?.transaction

  const keep = [x?.category?.id, x?.account?.id, x?.fromAccount?.id, x?.toAccount?.id, x?.paymentMode?.id]
  const accountsQ = useAccounts(!!edit)
  const modesQ = usePaymentModes(!!edit)
  const categoriesQ = useCategories(meta.direction, fund.id, !!edit)
  const accounts = { data: usable(accountsQ.data, keep) }
  const modes = { data: usable(modesQ.data, keep) }
  const categories = { data: usable(categoriesQ.data, keep) }
  const balances = useAccountBalances(kind === 'transfer' ? fund.id : undefined)

  const remembered = useMemo<Remembered>(() => (x
    ? { categoryId: x.category?.id, accountId: x.account?.id, modeId: x.paymentMode?.id, fromId: x.fromAccount?.id, toId: x.toAccount?.id }
    : recall(kind, fund.id)), [kind, fund.id, x])
  const [amount, setAmount] = useState(() => x?.amount ?? '')
  const [chosen, setChosen] = useState<Remembered>({})
  const [purposeText, setPurposeText] = useState<string | undefined>(() => x?.purpose ?? undefined)
  const [party, setParty] = useState(() => x?.receivedFrom ?? x?.paidTo ?? '')
  const [reference, setReference] = useState(() => x?.referenceNumber ?? '')
  const [remarks, setRemarks] = useState(() => x?.remarks ?? '')
  const [date, setDate] = useState(() => x?.txnDate ?? todayIso())
  const [time, setTime] = useState(() => x?.txnTime.slice(0, 5) ?? nowTime())
  const [reason, setReason] = useState('')
  const [moreOpen, setMoreOpen] = useState(!!edit)
  const [errors, setErrors] = useState<FieldErrors>({})
  const [banner, setBanner] = useState<string | null>(null)
  const [confirm, setConfirm] = useState(false)
  const [saved, setSaved] = useState<Result | null>(null)
  const [clientTxnId, setClientTxnId] = useState(() => crypto.randomUUID())

  // Effective selections: the user's pick, else last used (if still available), else the first option.
  const pick = (list: { id: string }[] | undefined, mine: string | undefined, last: string | undefined) =>
    list?.find((o) => o.id === mine)?.id ?? list?.find((o) => o.id === last)?.id ?? list?.[0]?.id ?? ''
  const categoryId = pick(categories.data, chosen.categoryId, remembered.categoryId)
  const accountId = pick(accounts.data, chosen.accountId, remembered.accountId)
  const modeId = pick(modes.data, chosen.modeId, remembered.modeId)
  const fromId = pick(accounts.data, chosen.fromId, remembered.fromId)
  const toId = (() => {
    const preferred = pick(accounts.data?.filter((a) => a.id !== fromId), chosen.toId, remembered.toId)
    return preferred
  })()

  const category = categories.data?.find((c) => c.id === categoryId)
  const mode = modes.data?.find((m) => m.id === modeId)
  const accountName = (id: string) => accounts.data?.find((a) => a.id === id)?.name ?? ''
  const balanceOf = (id: string) => balances.data?.find((b) => b.accountId === id)?.closing
  const purpose = purposeText ?? (kind === 'in' ? category?.name ?? '' : '')
  const needsReference = !!mode?.requiresReference

  const wire = toWireAmount(amount)
  const fromBalance = balanceOf(fromId)
  const lowBalance = kind === 'transfer' && !!wire && fromBalance !== undefined && compareAmounts(wire, fromBalance) > 0

  const save = useMutation({
    mutationFn: async (): Promise<Result> => {
      if (x) {
        return unwrap(await api.PUT('/api/v1/transactions/{id}', {
          params: { path: { id: x.id }, header: { 'If-Match': `"${x.revision}"` } },
          body: {
            amount: wire!, txnDate: date, txnTime: time,
            categoryId: kind === 'transfer' ? null : categoryId, accountId: kind === 'transfer' ? null : accountId,
            fromAccountId: kind === 'transfer' ? fromId : null, toAccountId: kind === 'transfer' ? toId : null,
            paymentModeId: kind === 'transfer' ? x.paymentMode?.id ?? null : modeId,
            receivedFrom: kind === 'in' ? party.trim() || null : null, paidTo: kind === 'out' ? party.trim() || null : null,
            purpose: purpose.trim(), referenceNumber: reference.trim() || null, remarks: remarks.trim() || null,
            adjustmentDirection: null, reason: reason.trim() || null,
          },
        }))
      }
      const common = { fundId: fund.id, amount: wire!, txnDate: date, txnTime: time, remarks: remarks.trim() || null, clientTxnId }
      if (kind === 'in') {
        return unwrap(await api.POST('/api/v1/transactions/deposit', {
          body: { ...common, categoryId, accountId, paymentModeId: modeId, receivedFrom: party.trim() || null, purpose: purpose.trim(), referenceNumber: reference.trim() || null },
        }))
      }
      if (kind === 'out') {
        return unwrap(await api.POST('/api/v1/transactions/expense', {
          body: { ...common, categoryId, accountId, paymentModeId: modeId, paidTo: party.trim() || null, purpose: purpose.trim(), referenceNumber: reference.trim() || null },
        }))
      }
      return unwrap(await api.POST('/api/v1/transactions/transfer', {
        body: { ...common, fromAccountId: fromId, toAccountId: toId, paymentModeId: null, purpose: purpose.trim(), referenceNumber: reference.trim() || null },
      }))
    },
    onSuccess: async (result) => {
      if (x) {
        setConfirm(false)
        toast.success(t('txn.updatedToast', { number: result.transaction.txnNumber }))
        await Promise.all(['dashboard', 'transactions', 'account-balances', 'transaction'].map((k) => queryClient.invalidateQueries({ queryKey: [k] })))
        void navigate(`/txn/${x.id}`, { replace: true })
        return
      }
      localStorage.setItem(memKey(kind, fund.id), JSON.stringify({ categoryId, accountId, modeId, fromId, toId } satisfies Remembered))
      setConfirm(false)
      setSaved(result)
      toast.success(t('txn.savedToast', { number: result.transaction.txnNumber }))
      await Promise.all(['dashboard', 'transactions', 'account-balances'].map((k) => queryClient.invalidateQueries({ queryKey: [k] })))
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
    if (kind !== 'transfer') {
      if (!categoryId) next.categoryId = [t('txn.errCategory')]
      if (!accountId) next.accountId = [t('txn.errAccount')]
      if (!modeId) next.paymentModeId = [t('txn.errMode')]
      if (needsReference && !reference.trim()) next.referenceNumber = [t('txn.errReference', { mode: mode?.name ?? '' })]
    } else {
      if (!fromId || !toId || fromId === toId) next.toAccountId = [t('txn.errTransferAccounts')]
    }
    if (!purpose.trim()) next.purpose = [t('txn.errPurpose')]
    if (edit?.editRequiresReason && !reason.trim()) next.reason = [t('txn.errReason')]
    setErrors(next)
    setBanner(null)
    if (Object.keys(next).length === 0) {
      setMoreOpen((o) => o || needsReference)
      setConfirm(true)
    } else {
      setMoreOpen(true)
    }
  }

  function addAnother() {
    setSaved(null)
    setAmount('')
    setPurposeText(undefined)
    setParty('')
    setReference('')
    setRemarks('')
    setClientTxnId(crypto.randomUUID()) // a NEW entry gets a NEW id
  }

  if (saved) {
    const tx = saved.transaction
    return (
      <div className="mx-auto grid max-w-xl gap-4 rounded-lg border border-border bg-surface p-6 text-center" role="status">
        <TxnTypeBadge type={tx.type} />
        <h1 className="text-2xl font-semibold">{t('txn.saved')}</h1>
        <p className="amount text-3xl font-bold">{formatRupees(tx.amount)}</p>
        <p className="text-text-muted">{tx.txnNumber}</p>
        <p className="text-sm">{t('txn.fundBalance', { balance: formatRupees(saved.fundClosingBalance) })}</p>
        <div className="flex flex-wrap justify-center gap-2">
          <Button onClick={addAnother}>{t('txn.addAnother')}</Button>
          <Button variant="secondary" onClick={() => void navigate(`/txn/${tx.id}`)}>{t('txn.view')}</Button>
          <Button variant="ghost" onClick={() => void navigate('/')}>{t('txn.done')}</Button>
        </div>
      </div>
    )
  }

  const err = (field: string) => errors[field]?.[0]
  const accountOptions = (accounts.data ?? []).map((a) => ({ id: a.id, label: a.name, hint: kind === 'transfer' && balanceOf(a.id) !== undefined ? formatRupees(balanceOf(a.id)!, { decimals: 'auto' }) : undefined }))
  const toOptions = accountOptions.filter((o) => o.id !== fromId)

  return (
    <div className="mx-auto grid max-w-xl gap-5">
      <div className="flex items-center gap-2">
        <Link to={x ? `/txn/${x.id}` : '/'} aria-label={t('actions.close')} className="flex size-11 items-center justify-center rounded-md hover:bg-surface-muted"><ArrowLeft aria-hidden className="size-5" /></Link>
        <div>
          <h1 className="text-xl font-semibold">{x ? t('txn.editTitle', { number: x.txnNumber }) : t(meta.title)}</h1>
          <p className="text-sm text-text-muted">{fund.name}</p>
        </div>
      </div>

      <form onSubmit={(e) => { e.preventDefault(); review() }} className="grid gap-5" noValidate>
        <ErrorBanner message={banner} />
        <AmountInput label={t('txn.amount')} value={amount} onChange={setAmount} error={err('amount')} accent={meta.accent} autoFocus />

        {kind !== 'transfer' ? (
          <>
            <ChipGroup label={t('txn.category')} required moreLabel={t('txn.more')} value={categoryId} onChange={(id) => setChosen((c) => ({ ...c, categoryId: id }))}
              options={(categories.data ?? []).map((c) => ({ id: c.id, label: c.name }))} error={err('categoryId')} />
            <ChipGroup label={t('txn.account')} required moreLabel={t('txn.more')} value={accountId} onChange={(id) => setChosen((c) => ({ ...c, accountId: id }))}
              options={accountOptions} error={err('accountId')} />
            <ChipGroup label={t('txn.paymentMode')} required moreLabel={t('txn.more')} value={modeId} onChange={(id) => setChosen((c) => ({ ...c, modeId: id }))}
              options={(modes.data ?? []).map((m) => ({ id: m.id, label: m.name }))} error={err('paymentModeId')} />
          </>
        ) : (
          <div className="grid gap-3">
            <ChipGroup label={t('txn.fromAccount')} required moreLabel={t('txn.more')} value={fromId} onChange={(id) => setChosen((c) => ({ ...c, fromId: id }))} options={accountOptions} />
            <div className="flex justify-center">
              <Button variant="ghost" aria-label={t('txn.swap')} onClick={() => setChosen((c) => ({ ...c, fromId: toId, toId: fromId }))}>
                <ArrowUpDown aria-hidden className="size-5" />
              </Button>
            </div>
            <ChipGroup label={t('txn.toAccount')} required moreLabel={t('txn.more')} value={toId} onChange={(id) => setChosen((c) => ({ ...c, toId: id }))} options={toOptions} error={err('toAccountId')} />
            {lowBalance && (
              <p role="status" className="flex items-start gap-2 rounded-md border border-warning bg-adjust-bg px-3 py-2 text-sm text-adjust">
                <TriangleAlert aria-hidden className="mt-0.5 size-4 shrink-0" />
                {t('txn.lowBalance', { account: accountName(fromId), balance: formatRupees(fromBalance!) })}
              </p>
            )}
          </div>
        )}

        <Field label={t('txn.purpose')} required value={purpose} onChange={(e) => setPurposeText(e.target.value)} error={err('purpose')} maxLength={300} />
        {kind !== 'transfer' && (
          <Field label={t(kind === 'in' ? 'txn.receivedFrom' : 'txn.paidTo')} value={party} onChange={(e) => setParty(e.target.value)} maxLength={120} />
        )}

        <button type="button" onClick={() => setMoreOpen((o) => !o)} aria-expanded={moreOpen}
          className="min-h-11 text-left text-sm font-medium text-primary">
          {t('txn.dateSummary', { date: date === todayIso() ? t('txn.today') : formatDate(date), time: formatTime(time) })} · {t('txn.change')}
        </button>
        {(moreOpen || needsReference) && (
          <div className="grid gap-4 rounded-lg border border-border p-3">
            <div className="grid grid-cols-2 gap-3">
              <Field label={t('txn.date')} type="date" required value={date} max={todayIso()} onChange={(e) => setDate(e.target.value)} error={err('txnDate')} />
              <Field label={t('txn.time')} type="time" required value={time} onChange={(e) => setTime(e.target.value)} error={err('txnTime')} />
            </div>
            <Field label={t('txn.reference')} required={needsReference} value={reference} onChange={(e) => setReference(e.target.value)} error={err('referenceNumber')} maxLength={80} />
            <Field label={t('txn.remarks')} value={remarks} onChange={(e) => setRemarks(e.target.value)} />
          </div>
        )}

        {edit?.editRequiresReason && (
          <Field label={t('txn.editReason')} required value={reason} onChange={(e) => setReason(e.target.value)} error={err('reason')} maxLength={500}
            hint={t('txn.editReasonHint')} />
        )}

        <Button type="submit" size="lg" className="sticky bottom-20 lg:static">{t('txn.review')}</Button>
      </form>

      <Dialog open={confirm} onClose={() => setConfirm(false)} title={t('txn.confirmTitle', { type: t(`txn.type.${meta.type}`) })}
        footer={<>
          <Button variant="secondary" onClick={() => setConfirm(false)}>{t('txn.edit')}</Button>
          <Button loading={save.isPending} onClick={() => save.mutate()}>{x ? t('txn.saveChanges') : t(meta.save)}</Button>
        </>}>
        <p className="amount text-center text-3xl font-bold" aria-label={`${t(`txn.type.${meta.type}`)} ${wire ?? ''}`}>{wire ? formatRupees(wire) : ''}</p>
        <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm">
          <dt className="text-text-muted">{t('txn.fund')}</dt><dd>{fund.name}</dd>
          {kind === 'transfer' ? (
            <><dt className="text-text-muted">{t('txn.accounts')}</dt><dd>{accountName(fromId)} → {accountName(toId)}</dd></>
          ) : (
            <>
              <dt className="text-text-muted">{t('txn.category')}</dt><dd>{category?.name}</dd>
              <dt className="text-text-muted">{t('txn.account')}</dt><dd>{accountName(accountId)}</dd>
              <dt className="text-text-muted">{t('txn.paymentMode')}</dt><dd>{mode?.name}</dd>
            </>
          )}
          <dt className="text-text-muted">{t('txn.when')}</dt><dd>{formatDate(date)} {formatTime(time)}</dd>
          <dt className="text-text-muted">{t('txn.purpose')}</dt><dd>{purpose}</dd>
          {reference && <><dt className="text-text-muted">{t('txn.reference')}</dt><dd>{reference}</dd></>}
          {reason.trim() && <><dt className="text-text-muted">{t('txn.editReason')}</dt><dd>{reason.trim()}</dd></>}
        </dl>
        {kind === 'transfer' && <p className="text-sm text-text-muted">{t('txn.fundUnchanged')}</p>}
      </Dialog>
    </div>
  )
}
