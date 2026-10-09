import { useState, type FormEvent } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { ArrowLeft } from 'lucide-react'
import { api, type Schemas } from '../../lib/api/client'
import { ApiError, unwrap, type FieldErrors } from '../../lib/api/errors'
import { useFundTypes } from '../../lib/api/hooks'
import { todayIso } from '../../lib/format/date'
import { sanitizeAmount, toWireAmount } from '../../lib/format/money'
import { Badge, Button, Dialog, ErrorBanner, Field } from '../../components/ui'
import { Section } from '../../components/ui/money'
import { toast } from '../../lib/toast'

type Detail = Schemas['FundDetail']
const TONE = { DRAFT: 'muted', ACTIVE: 'success', CLOSED: 'warning', ARCHIVED: 'muted' } as const

/** S21 — create/edit a fund, move it through its lifecycle, set opening balances (App Flow §5.2). */
export function FundFormPage() {
  const { t } = useTranslation()
  const { id } = useParams()
  const isNew = !id || id === 'new'
  const fund = useQuery({
    queryKey: ['fund', id],
    enabled: !isNew,
    queryFn: async () => unwrap(await api.GET('/api/v1/funds/{id}', { params: { path: { id: id! } } })),
  })
  if (!isNew && fund.isError) return <ErrorBanner message={fund.error.message} />
  if (!isNew && !fund.data) return <p className="text-text-muted">{t('app.loading')}</p>
  // Keyed by the loaded record so the form state is initialised once from it (and again after a reload of the same fund).
  return <FundForm key={`${id ?? 'new'}:${fund.dataUpdatedAt}`} detail={fund.data} />
}

function FundForm({ detail }: { detail: Detail | undefined }) {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const types = useFundTypes()
  const f = detail?.fund
  const isNew = !f

  const [code, setCode] = useState(f?.code ?? '')
  const [name, setName] = useState(f?.name ?? '')
  const [typeId, setTypeId] = useState(f?.fundTypeId ?? '')
  const [description, setDescription] = useState(f?.description ?? '')
  const [start, setStart] = useState(f?.startDate ?? '')
  const [end, setEnd] = useState(f?.endDate ?? '')
  const [errors, setErrors] = useState<FieldErrors>({})
  const [error, setError] = useState<string | null>(null)
  const [dialog, setDialog] = useState<'close' | 'reopen' | 'archive' | null>(null)
  const [reason, setReason] = useState('')

  const closedLike = f?.status === 'CLOSED' || f?.status === 'ARCHIVED'
  const effectiveType = typeId || types.data?.[0]?.id || ''

  const onError = (e: unknown) => {
    if (e instanceof ApiError) { setErrors(e.fieldErrors); setError(e.message) } else setError(t('errors.generic'))
  }
  const refresh = async () => {
    await Promise.all(['fund', 'funds', 'me', 'dashboard'].map((k) => queryClient.invalidateQueries({ queryKey: [k] })))
  }

  const save = useMutation({
    mutationFn: async () => {
      setError(null); setErrors({})
      const body = { code, name, fundTypeId: effectiveType, description: description || null, startDate: start || null, endDate: end || null }
      return f
        ? unwrap(await api.PUT('/api/v1/funds/{id}', { params: { path: { id: f.id } }, body }))
        : unwrap(await api.POST('/api/v1/funds', { body }))
    },
    onSuccess: async (d) => {
      toast.success(t('master.saved'))
      await refresh()
      if (isNew) void navigate(`/admin/funds/${d.fund.id}`, { replace: true })
    },
    onError,
  })

  const transition = useMutation({
    mutationFn: async (action: 'activate' | 'close' | 'reopen' | 'archive') => {
      const path = { params: { path: { id: f!.id } } }
      switch (action) {
        case 'activate': return unwrap(await api.POST('/api/v1/funds/{id}/activate', path))
        case 'close': return unwrap(await api.POST('/api/v1/funds/{id}/close', { ...path, body: { reason: reason || null } }))
        case 'reopen': return unwrap(await api.POST('/api/v1/funds/{id}/reopen', { ...path, body: { reason } }))
        case 'archive': return unwrap(await api.POST('/api/v1/funds/{id}/archive', path))
      }
    },
    onSuccess: async () => { setDialog(null); setReason(''); toast.success(t('master.saved')); await refresh() },
    onError: (e) => { setDialog(null); onError(e) },
  })

  const submit = (e: FormEvent) => { e.preventDefault(); save.mutate() }

  return (
    <div className="mx-auto grid max-w-3xl gap-6">
      <div className="flex items-center gap-2">
        <Link to="/admin/funds" aria-label={t('screens.funds')} className="flex size-11 items-center justify-center rounded-md hover:bg-surface-muted"><ArrowLeft aria-hidden className="size-5" /></Link>
        <h1 className="text-2xl font-semibold">{f?.name ?? t('funds.create')}</h1>
        {f && <Badge tone={TONE[f.status]}>{t(`funds.status.${f.status}`)}</Badge>}
      </div>
      <ErrorBanner message={error} />

      <form onSubmit={submit} className="grid gap-4 rounded-lg border border-border bg-surface p-4 sm:grid-cols-2" noValidate>
        <Field label={t('funds.code')} required value={code} disabled={closedLike || detail?.hasTransactions} onChange={(e) => setCode(e.target.value.toUpperCase())} maxLength={10}
          error={errors.code?.[0]} hint={detail?.hasTransactions ? t('funds.codeLocked') : t('funds.codeHint')} />
        <Field label={t('master.name')} required value={name} disabled={closedLike} onChange={(e) => setName(e.target.value)} maxLength={120} error={errors.name?.[0]} />
        <div className="grid gap-1">
          <label htmlFor="fund-type" className="text-sm font-medium">{t('funds.type')}</label>
          <select id="fund-type" value={effectiveType} disabled={closedLike} onChange={(e) => setTypeId(e.target.value)} className="min-h-11 rounded-md border border-border bg-surface px-3">
            {types.data?.map((ft) => <option key={ft.id} value={ft.id}>{ft.name}</option>)}
          </select>
          {errors.fundTypeId?.[0] && <p role="alert" className="text-sm text-danger">{errors.fundTypeId[0]}</p>}
        </div>
        <div className="grid grid-cols-2 gap-3">
          <Field label={t('funds.start')} type="date" value={start} disabled={closedLike} onChange={(e) => setStart(e.target.value)} />
          <Field label={t('funds.end')} type="date" value={end} disabled={closedLike} onChange={(e) => setEnd(e.target.value)} error={errors.endDate?.[0]} />
        </div>
        <Field className="sm:col-span-2" label={t('funds.description')} value={description} disabled={f?.status === 'ARCHIVED'} onChange={(e) => setDescription(e.target.value)} />
        {f?.status !== 'ARCHIVED' && <Button type="submit" size="lg" loading={save.isPending} className="sm:col-span-2">{isNew ? t('funds.create') : t('actions.save')}</Button>}
      </form>

      {f && (
        <>
          <Section title={t('funds.lifecycle')} id="life-h">
            <p className="text-sm text-text-muted">{t(`funds.help.${f.status}`)}</p>
            <div className="flex flex-wrap gap-2">
              {f.status === 'DRAFT' && <Button loading={transition.isPending} onClick={() => transition.mutate('activate')}>{t('funds.activate')}</Button>}
              {f.status === 'ACTIVE' && <Button variant="secondary" onClick={() => setDialog('close')}>{t('funds.close')}</Button>}
              {f.status === 'CLOSED' && <Button onClick={() => setDialog('reopen')}>{t('funds.reopen')}</Button>}
              {f.status === 'CLOSED' && <Button variant="secondary" onClick={() => setDialog('archive')}>{t('funds.archive')}</Button>}
            </div>
          </Section>
          {(f.status === 'DRAFT' || f.status === 'ACTIVE') && <OpeningBalances detail={detail!} onSaved={refresh} />}
        </>
      )}

      <Dialog open={dialog === 'close'} onClose={() => setDialog(null)} title={t('funds.closeTitle', { name: f?.name ?? '' })}
        footer={<><Button variant="secondary" onClick={() => setDialog(null)}>{t('actions.cancel')}</Button>
          <Button loading={transition.isPending} onClick={() => transition.mutate('close')}>{t('funds.close')}</Button></>}>
        <p>{t('funds.closeBody', { balance: f?.closingBalance ?? '' })}</p>
        <Field label={t('funds.reasonOptional')} value={reason} onChange={(e) => setReason(e.target.value)} maxLength={300} />
      </Dialog>
      <Dialog open={dialog === 'reopen'} onClose={() => setDialog(null)} title={t('funds.reopenTitle', { name: f?.name ?? '' })}
        footer={<><Button variant="secondary" onClick={() => setDialog(null)}>{t('actions.cancel')}</Button>
          <Button disabled={!reason.trim()} loading={transition.isPending} onClick={() => transition.mutate('reopen')}>{t('funds.reopen')}</Button></>}>
        <Field label={t('funds.reasonRequired')} required value={reason} onChange={(e) => setReason(e.target.value)} maxLength={300} />
      </Dialog>
      <Dialog open={dialog === 'archive'} onClose={() => setDialog(null)} title={t('funds.archiveTitle', { name: f?.name ?? '' })}
        footer={<><Button variant="secondary" onClick={() => setDialog(null)}>{t('actions.cancel')}</Button>
          <Button loading={transition.isPending} onClick={() => transition.mutate('archive')}>{t('funds.archive')}</Button></>}>
        <p>{t('funds.archiveBody')}</p>
      </Dialog>
    </div>
  )
}

/** Opening balance per account (BR-015). On an ACTIVE fund every change needs a reason and is audited. */
function OpeningBalances({ detail, onSaved }: { detail: Detail; onSaved: () => Promise<void> }) {
  const { t } = useTranslation()
  const [amounts, setAmounts] = useState<Record<string, string>>(() =>
    Object.fromEntries(detail.openingBalances.map((o) => [o.accountId, o.amount === '0.00' ? '' : o.amount.replace(/\.00$/, '')])))
  const [asOf, setAsOf] = useState(detail.openingBalances.find((o) => o.asOfDate)?.asOfDate ?? todayIso())
  const [reason, setReason] = useState('')
  const [error, setError] = useState<string | null>(null)
  const active = detail.fund.status === 'ACTIVE'

  const save = useMutation({
    mutationFn: async () => {
      setError(null)
      const items = detail.openingBalances.map((o) => ({ accountId: o.accountId, amount: toWireAmount(amounts[o.accountId] || '0') ?? '0.00', asOfDate: asOf }))
      return unwrap(await api.PUT('/api/v1/funds/{id}/opening-balances', { params: { path: { id: detail.fund.id } }, body: { items, reason: reason || null } }))
    },
    onSuccess: async () => { setReason(''); toast.success(t('master.saved')); await onSaved() },
    onError: (e) => setError(e instanceof ApiError ? e.message : t('errors.generic')),
  })

  return (
    <Section title={t('funds.opening')} id="open-h">
      <p className="text-sm text-text-muted">{t(active ? 'funds.openingActiveHelp' : 'funds.openingDraftHelp')}</p>
      <ErrorBanner message={error} />
      <ul className="grid gap-3">
        {detail.openingBalances.map((o) => (
          <li key={o.accountId}>
            <Field label={`${o.accountName}${o.accountActive ? '' : ` (${t('master.inactive')})`}`} prefix="₹" inputMode="decimal" value={amounts[o.accountId] ?? ''}
              onChange={(e) => setAmounts((a) => ({ ...a, [o.accountId]: sanitizeAmount(e.target.value) }))} placeholder="0" />
          </li>
        ))}
      </ul>
      <Field label={t('funds.asOf')} type="date" value={asOf} onChange={(e) => setAsOf(e.target.value)} />
      {active && <Field label={t('funds.reasonRequired')} value={reason} onChange={(e) => setReason(e.target.value)} maxLength={300} hint={t('funds.reasonHint')} />}
      <Button loading={save.isPending} onClick={() => save.mutate()}>{t('funds.saveOpening')}</Button>
    </Section>
  )
}
