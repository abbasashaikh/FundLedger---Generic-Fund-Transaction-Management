import { useState, type ReactNode } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { api, type Schemas } from '../../lib/api/client'
import { ApiError, unwrap, type FieldErrors } from '../../lib/api/errors'
import { Button, Checkbox, Dialog, ErrorBanner, Field } from '../../components/ui'
import { toast } from '../../lib/toast'

type Settings = Schemas['SettingsDto']

const DATE_FORMATS = ['dd-MMM-yyyy', 'dd/MM/yyyy', 'yyyy-MM-dd'] as const

/** Flattens the nested settings into "group.field" → value, for the "what will change" list. */
function flatten(s: Settings): Record<string, string> {
  const out: Record<string, string> = {}
  for (const [group, values] of Object.entries(s)) {
    for (const [key, value] of Object.entries(values as Record<string, unknown>)) {
      out[`${group}.${key}`] = value === null || value === undefined ? '' : String(value)
    }
  }
  return out
}

/** S26 — settings (App Flow §5.5). Saving lists what will change first, and every change is audited. */
export function SettingsPage() {
  const { t } = useTranslation()
  const saved = useQuery<Settings, ApiError>({
    queryKey: ['settings'],
    queryFn: async () => unwrap(await api.GET('/api/v1/settings')),
  })

  if (saved.isPending) return <p className="text-text-muted">{t('app.loading')}</p>
  if (saved.isError) return <ErrorBanner message={saved.error.message} />
  // Keyed by the saved version, so the form restarts from what the server now holds.
  return <SettingsForm key={JSON.stringify(saved.data)} saved={saved.data} />
}

function SettingsForm({ saved }: { saved: Settings }) {
  const { t } = useTranslation()
  const queryClient = useQueryClient()
  const [draft, setDraft] = useState<Settings>(saved)
  const [errors, setErrors] = useState<FieldErrors>({})
  const [banner, setBanner] = useState<string | null>(null)
  const [confirm, setConfirm] = useState(false)

  const was = flatten(saved)
  const now = flatten(draft)
  const changed = Object.keys(now).filter((k) => was[k] !== now[k])

  const save = useMutation({
    mutationFn: async () => unwrap(await api.PUT('/api/v1/settings', { body: draft })),
    onSuccess: async (result) => {
      setConfirm(false)
      queryClient.setQueryData(['settings'], result)
      await queryClient.invalidateQueries({ queryKey: ['me'] })
      toast.success(t('settings.saved'))
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

  const org = (patch: Partial<Settings['organization']>) => setDraft((d) => ({ ...d, organization: { ...d.organization, ...patch } }))
  const txn = (patch: Partial<Settings['transactions']>) => setDraft((d) => ({ ...d, transactions: { ...d.transactions, ...patch } }))
  const auth = (patch: Partial<Settings['auth']>) => setDraft((d) => ({ ...d, auth: { ...d.auth, ...patch } }))
  const rcpt = (patch: Partial<Settings['receipts']>) => setDraft((d) => ({ ...d, receipts: { ...d.receipts, ...patch } }))
  const num = (v: string) => (v === '' ? 0 : Number(v.replace(/\D/g, '')))
  // The API names nested fields like "Organization.Name"; match case-insensitively.
  const byName = Object.fromEntries(Object.entries(errors).map(([k, v]) => [k.toLowerCase(), v]))
  const err = (field: string) => byName[field.toLowerCase()]?.[0]

  return (
    <div className="mx-auto grid max-w-2xl gap-5">
      <h1 className="text-2xl font-semibold">{t('screens.settings')}</h1>
      <form onSubmit={(e) => { e.preventDefault(); setErrors({}); setBanner(null); setConfirm(true) }} className="grid gap-5" noValidate>
        <ErrorBanner message={banner} />

        <Group title={t('settings.organization')}>
          <Field label={t('settings.orgName')} required value={draft.organization.name} onChange={(e) => org({ name: e.target.value })} error={err('organization.name')} maxLength={150} />
          <Field label={t('settings.contactMobile')} value={draft.organization.contactMobile ?? ''} onChange={(e) => org({ contactMobile: e.target.value || null })} maxLength={15} />
          <Field label={t('settings.contactEmail')} type="email" value={draft.organization.contactEmail ?? ''} onChange={(e) => org({ contactEmail: e.target.value || null })} error={err('organization.contactEmail')} />
          <Field label={t('settings.address')} value={draft.organization.address ?? ''} onChange={(e) => org({ address: e.target.value || null })} maxLength={500} />
          <Field label={t('settings.registrationNumber')} value={draft.organization.registrationNumber ?? ''} onChange={(e) => org({ registrationNumber: e.target.value || null })}
            hint={t('settings.registrationHint')} maxLength={60} />
          <label className="grid gap-1 text-sm font-medium">
            {t('settings.dateFormat')}
            <select value={draft.organization.dateFormat} onChange={(e) => org({ dateFormat: e.target.value })} className="min-h-11 rounded-md border border-border bg-surface px-2 font-normal">
              {DATE_FORMATS.map((f) => <option key={f} value={f}>{f}</option>)}
            </select>
          </label>
          <Field label={t('settings.timezone')} value={draft.organization.timezone} onChange={(e) => org({ timezone: e.target.value })} error={err('organization.timezone')} />
          <p className="text-xs text-text-muted">{t('settings.currencyLocked')}</p>
        </Group>

        <Group title={t('settings.transactions')}>
          <Field label={t('settings.editWindow')} inputMode="numeric" value={String(draft.transactions.editWindowMinutes)} onChange={(e) => txn({ editWindowMinutes: num(e.target.value) })}
            error={err('transactions.editWindowMinutes')} hint={t('settings.editWindowHint')} />
          <Field label={t('settings.backdate')} inputMode="numeric" value={String(draft.transactions.backdateDaysMember)} onChange={(e) => txn({ backdateDaysMember: num(e.target.value) })}
            error={err('transactions.backdateDaysMember')} />
          <Field label={t('settings.maxAmount')} inputMode="decimal" value={draft.transactions.maxAmount} onChange={(e) => txn({ maxAmount: e.target.value.replace(/[^\d.]/g, '') })}
            error={err('transactions.maxAmount')} />
          <p className="text-xs text-text-muted">{t('settings.numberingLocked')}</p>
        </Group>

        <Group title={t('settings.auth')}>
          <Field label={t('settings.pinFailures')} inputMode="numeric" value={String(draft.auth.pinMaxFailures)} onChange={(e) => auth({ pinMaxFailures: num(e.target.value) })} error={err('auth.pinMaxFailures')} hint="3 – 10" />
          <Field label={t('settings.lockoutMinutes')} inputMode="numeric" value={String(draft.auth.pinLockoutMinutes)} onChange={(e) => auth({ pinLockoutMinutes: num(e.target.value) })} error={err('auth.pinLockoutMinutes')} hint="5 – 1440" />
          <Field label={t('settings.idleMinutes')} inputMode="numeric" value={String(draft.auth.sessionIdleMinutes)} onChange={(e) => auth({ sessionIdleMinutes: num(e.target.value) })} error={err('auth.sessionIdleMinutes')} hint="15 – 1440" />
          <Field label={t('settings.sessionDays')} inputMode="numeric" value={String(draft.auth.sessionAbsoluteDays)} onChange={(e) => auth({ sessionAbsoluteDays: num(e.target.value) })} error={err('auth.sessionAbsoluteDays')} hint="1 – 30" />
        </Group>

        <Group title={t('settings.receipts')}>
          <Checkbox label={t('settings.receiptsEnabled')} checked={draft.receipts.enabled} onChange={(v) => rcpt({ enabled: v })} />
          <Checkbox label={t('settings.showRecordedBy')} checked={draft.receipts.showRecordedBy} onChange={(v) => rcpt({ showRecordedBy: v })} />
          <Field label={t('settings.footerText')} value={draft.receipts.footerText} onChange={(e) => rcpt({ footerText: e.target.value })} error={err('receipts.footerText')} maxLength={200} />
          <p className="text-xs text-text-muted">{t('settings.noTaxClaim')}</p>
        </Group>

        <Button type="submit" size="lg" disabled={changed.length === 0}>{t('actions.save')}</Button>
      </form>

      <Dialog open={confirm} onClose={() => setConfirm(false)} title={t('settings.confirmTitle')}
        footer={<>
          <Button variant="secondary" onClick={() => setConfirm(false)}>{t('actions.cancel')}</Button>
          <Button loading={save.isPending} onClick={() => save.mutate()}>{t('settings.confirmSave')}</Button>
        </>}>
        <ul className="grid gap-1 text-sm">
          {changed.map((k) => (
            <li key={k}>
              <span className="text-text-muted">{t(`settings.fields.${k}`, { defaultValue: k })}: </span>
              <del className="text-text-muted">{was[k] || '—'}</del>{' → '}<ins className="font-medium no-underline">{now[k] || '—'}</ins>
            </li>
          ))}
        </ul>
      </Dialog>
    </div>
  )
}

function Group({ title, children }: { title: string; children: ReactNode }) {
  return (
    <fieldset className="grid gap-3 rounded-lg border border-border bg-surface p-4">
      <legend className="px-1 font-semibold">{title}</legend>
      {children}
    </fieldset>
  )
}
