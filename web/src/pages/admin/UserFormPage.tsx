import { useState, type FormEvent } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { ArrowLeft, Copy, KeyRound, LogOut, UserCheck, UserX } from 'lucide-react'
import { api, type Schemas } from '../../lib/api/client'
import { ApiError, unwrap } from '../../lib/api/errors'
import { Badge, Button, Checkbox, Dialog, ErrorBanner, Field } from '../../components/ui'
import { useSession } from '../../lib/auth/session'
import { formatDateTime } from '../../lib/format/date'

type Grant = Schemas['FundAccessGrant']
type Flags = Omit<Grant, 'fundId'>

// Presets from App Flow §5.1 — a starting point the Admin can adjust per fund.
const PRESETS: Record<string, Flags> = {
  collector: { canMoneyIn: true, canMoneyOut: false, canTransfer: false, canViewReports: true, canExport: false, canViewAllTxns: true },
  spender: { canMoneyIn: false, canMoneyOut: true, canTransfer: false, canViewReports: true, canExport: false, canViewAllTxns: true },
  treasurer: { canMoneyIn: true, canMoneyOut: true, canTransfer: true, canViewReports: true, canExport: true, canViewAllTxns: true },
  viewer: { canMoneyIn: false, canMoneyOut: false, canTransfer: false, canViewReports: true, canExport: false, canViewAllTxns: true },
}

const FLAG_KEYS: (keyof Flags)[] = ['canMoneyIn', 'canMoneyOut', 'canTransfer', 'canViewReports', 'canExport', 'canViewAllTxns']

type UserDetail = Schemas['UserDetail']

/** S19 — create/edit a user, fund access grid, status, PIN reset, sessions (App Flow §5.1). */
export function UserFormPage() {
  const { t } = useTranslation()
  const { id } = useParams()
  const isNew = !id || id === 'new'
  const user = useQuery({
    queryKey: ['user', id],
    enabled: !isNew,
    queryFn: async () => unwrap(await api.GET('/api/v1/users/{id}', { params: { path: { id: id! } } })),
  })

  if (!isNew && user.isError) return <ErrorBanner message={user.error.message} />
  if (!isNew && !user.data) return <p className="text-text-muted">{t('app.loading')}</p>
  // Keyed by user id so the form's state is initialised once from the loaded record.
  return <UserForm key={id ?? 'new'} id={isNew ? undefined : id} initial={user.data} />
}

function UserForm({ id, initial }: { id: string | undefined; initial: UserDetail | undefined }) {
  const { t } = useTranslation()
  const isNew = id === undefined
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const selfId = useSession((s) => s.user?.id)
  const funds = useQuery({ queryKey: ['funds'], queryFn: async () => unwrap(await api.GET('/api/v1/funds')) })

  const [fullName, setFullName] = useState(initial?.fullName ?? '')
  const [mobile, setMobile] = useState(initial?.mobile.replace(/^\+91/, '') ?? '')
  const [email, setEmail] = useState(initial?.email ?? '')
  const [role, setRole] = useState<'ADMIN' | 'MEMBER'>(initial?.role ?? 'MEMBER')
  const [grants, setGrants] = useState<Record<string, Flags>>(
    () => Object.fromEntries((initial?.fundAccess ?? []).map(({ fundId, ...flags }) => [fundId, flags])))
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({})
  const [error, setError] = useState<string | null>(null)
  const [revealPin, setRevealPin] = useState<{ name: string; pin: string } | null>(null)
  const [confirm, setConfirm] = useState<'deactivate' | 'reset' | null>(null)

  const grantList = (): Grant[] => Object.entries(grants).map(([fundId, flags]) => ({ fundId, ...flags }))

  const onError = (err: unknown) => {
    if (err instanceof ApiError) {
      setFieldErrors(err.fieldErrors)
      setError(err.message)
    } else {
      setError(t('errors.generic'))
    }
  }

  const save = useMutation({
    mutationFn: async () => {
      setError(null)
      setFieldErrors({})
      if (isNew) {
        return unwrap(await api.POST('/api/v1/users', {
          body: { fullName, mobile, email: email || null, role, fundAccess: role === 'MEMBER' ? grantList() : [] },
        }))
      }

      const updated = unwrap(await api.PUT('/api/v1/users/{id}', {
        params: { path: { id: id! } },
        body: { fullName, mobile, email: email || null, role, version: initial!.version },
      }))
      if (role === 'MEMBER') {
        return unwrap(await api.PUT('/api/v1/users/{id}/fund-access', { params: { path: { id: id! } }, body: { items: grantList() } }))
      }

      return updated
    },
    onSuccess: async (result) => {
      await queryClient.invalidateQueries({ queryKey: ['users'] })
      if (isNew && 'temporaryPin' in result) {
        setRevealPin({ name: result.user.fullName, pin: result.temporaryPin })
        void navigate(`/admin/users/${result.user.id}`, { replace: true })
      } else {
        await queryClient.invalidateQueries({ queryKey: ['user', id] })
      }
    },
    onError,
  })

  const setStatus = useMutation({
    mutationFn: async (status: 'ACTIVE' | 'INACTIVE') =>
      unwrap(await api.PATCH('/api/v1/users/{id}/status', { params: { path: { id: id! } }, body: { status, reason: null } })),
    onSuccess: async () => {
      setConfirm(null)
      await Promise.all([queryClient.invalidateQueries({ queryKey: ['user', id] }), queryClient.invalidateQueries({ queryKey: ['users'] })])
    },
    onError: (e) => { setConfirm(null); onError(e) },
  })

  const resetPin = useMutation({
    mutationFn: async () => unwrap(await api.POST('/api/v1/users/{id}/reset-pin', { params: { path: { id: id! } } })),
    onSuccess: async (r) => {
      setConfirm(null)
      setRevealPin({ name: fullName, pin: r.temporaryPin })
      await queryClient.invalidateQueries({ queryKey: ['user', id] })
    },
    onError: (e) => { setConfirm(null); onError(e) },
  })

  const submit = (e: FormEvent) => {
    e.preventDefault()
    save.mutate()
  }

  const toggleFund = (fundId: string, on: boolean) =>
    setGrants((g) => {
      const next = { ...g }
      if (on) next[fundId] = PRESETS.collector!
      else delete next[fundId]
      return next
    })

  const applyPreset = (fundId: string, preset: string) =>
    setGrants((g) => ({ ...g, [fundId]: PRESETS[preset]! }))

  const isSelf = !isNew && id === selfId
  const u = initial

  return (
    <div className="mx-auto grid max-w-3xl gap-6">
      <div className="flex items-center gap-2">
        <Link to="/admin/users" className="flex size-11 items-center justify-center rounded-md hover:bg-surface-muted" aria-label={t('users.backToList')}>
          <ArrowLeft aria-hidden className="size-5" />
        </Link>
        <h1 className="text-2xl font-semibold">{isNew ? t('users.add') : u?.fullName ?? '…'}</h1>
        {u?.status === 'INACTIVE' && <Badge tone="danger">{t('users.statusInactive')}</Badge>}
        {u?.pinMustChange && u.status === 'ACTIVE' && <Badge tone="warning">{t('users.pinPending')}</Badge>}
      </div>

      <form onSubmit={submit} className="grid gap-6" noValidate>
        <ErrorBanner message={error} />

        <section className="grid gap-4 rounded-lg border border-border bg-surface p-4 sm:grid-cols-2" aria-labelledby="identity-h">
          <h2 id="identity-h" className="font-semibold sm:col-span-2">{t('users.identity')}</h2>
          <Field label={t('users.fullName')} required value={fullName} onChange={(e) => setFullName(e.target.value)} error={fieldErrors.fullName?.[0]} />
          <Field label={t('auth.mobile')} prefix="+91" inputMode="numeric" required value={mobile} onChange={(e) => setMobile(e.target.value)} error={fieldErrors.mobile?.[0]} />
          <Field label={t('users.email')} type="email" value={email} onChange={(e) => setEmail(e.target.value)} error={fieldErrors.email?.[0]} />
          <div className="grid gap-1">
            <label htmlFor="role" className="text-sm font-medium">{t('users.role')}</label>
            <select id="role" value={role} disabled={isSelf} onChange={(e) => setRole(e.target.value as typeof role)}
              className="min-h-11 rounded-md border border-border bg-surface px-3">
              <option value="MEMBER">{t('users.roleMember')}</option>
              <option value="ADMIN">{t('users.roleAdmin')}</option>
            </select>
            {isSelf && <p className="text-xs text-text-muted">{t('users.cannotChangeOwnRole')}</p>}
          </div>
        </section>

        <section className="grid gap-3 rounded-lg border border-border bg-surface p-4" aria-labelledby="access-h">
          <h2 id="access-h" className="font-semibold">{t('users.fundAccess')}</h2>
          {role === 'ADMIN' ? (
            <p className="text-sm text-text-muted">{t('users.adminAllFunds')}</p>
          ) : funds.data?.length === 0 ? (
            <p className="text-sm text-text-muted">{t('users.noFundsYet')}</p>
          ) : (
            <ul className="grid gap-3">
              {funds.data?.map((f) => {
                const flags = grants[f.id]
                return (
                  <li key={f.id} className="rounded-md border border-border p-3">
                    <div className="flex flex-wrap items-center gap-2">
                      <Checkbox label={`${f.name} (${f.code})`} checked={!!flags} onChange={(on) => toggleFund(f.id, on)} />
                      {f.status !== 'ACTIVE' && <Badge>{t(`funds.status.${f.status}`)}</Badge>}
                      {flags && (
                        <select aria-label={t('users.preset')} defaultValue="" onChange={(e) => e.target.value && applyPreset(f.id, e.target.value)}
                          className="ml-auto min-h-11 rounded-md border border-border bg-surface px-2 text-sm">
                          <option value="" disabled>{t('users.preset')}</option>
                          {Object.keys(PRESETS).map((p) => <option key={p} value={p}>{t(`users.presets.${p}`)}</option>)}
                        </select>
                      )}
                    </div>
                    {flags && (
                      <div className="mt-1 grid grid-cols-2 gap-x-4 sm:grid-cols-3">
                        {FLAG_KEYS.map((k) => (
                          <Checkbox key={k} label={t(`users.flags.${k}`)} checked={flags[k] ?? false}
                            onChange={(v) => setGrants((g) => ({ ...g, [f.id]: { ...g[f.id]!, [k]: v } }))} />
                        ))}
                      </div>
                    )}
                  </li>
                )
              })}
            </ul>
          )}
        </section>

        <div className="flex flex-wrap gap-2">
          <Button type="submit" size="lg" loading={save.isPending}>{isNew ? t('users.create') : t('actions.save')}</Button>
        </div>
      </form>

      {!isNew && u && (
        <>
          <section className="grid gap-3 rounded-lg border border-border bg-surface p-4" aria-labelledby="security-h">
            <h2 id="security-h" className="font-semibold">{t('users.security')}</h2>
            <p className="text-sm text-text-muted">
              {u.lastLoginAt ? t('users.lastLogin', { when: formatDateTime(u.lastLoginAt) }) : t('users.neverSignedIn')}
            </p>
            <div className="flex flex-wrap gap-2">
              {!isSelf && (
                <Button variant="secondary" onClick={() => setConfirm('reset')}><KeyRound aria-hidden className="size-4" />{t('users.resetPin')}</Button>
              )}
              {!isSelf && u.status === 'ACTIVE' && (
                <Button variant="destructive" onClick={() => setConfirm('deactivate')}><UserX aria-hidden className="size-4" />{t('users.deactivate')}</Button>
              )}
              {u.status === 'INACTIVE' && (
                <Button variant="secondary" loading={setStatus.isPending} onClick={() => setStatus.mutate('ACTIVE')}>
                  <UserCheck aria-hidden className="size-4" />{t('users.activate')}
                </Button>
              )}
            </div>
            {u.status === 'ACTIVE' && <SessionsPanel userId={u.id} />}
          </section>
        </>
      )}

      <Dialog open={confirm === 'deactivate'} onClose={() => setConfirm(null)} title={t('users.deactivateTitle', { name: fullName })}
        footer={<>
          <Button variant="secondary" onClick={() => setConfirm(null)}>{t('actions.cancel')}</Button>
          <Button variant="destructive" loading={setStatus.isPending} onClick={() => setStatus.mutate('INACTIVE')}>{t('users.deactivate')}</Button>
        </>}>
        <p>{t('users.deactivateBody', { name: fullName })}</p>
      </Dialog>

      <Dialog open={confirm === 'reset'} onClose={() => setConfirm(null)} title={t('users.resetPinTitle', { name: fullName })}
        footer={<>
          <Button variant="secondary" onClick={() => setConfirm(null)}>{t('actions.cancel')}</Button>
          <Button loading={resetPin.isPending} onClick={() => resetPin.mutate()}>{t('users.resetPin')}</Button>
        </>}>
        <p>{t('users.resetPinBody')}</p>
      </Dialog>

      <TemporaryPinDialog value={revealPin} onClose={() => setRevealPin(null)} />
    </div>
  )
}

/** Shows a one-time temporary PIN for the Admin to hand over in person (ADR-0002). */
function TemporaryPinDialog({ value, onClose }: { value: { name: string; pin: string } | null; onClose: () => void }) {
  const { t } = useTranslation()
  const [copied, setCopied] = useState(false)
  return (
    <Dialog open={!!value} onClose={() => { setCopied(false); onClose() }} title={t('users.tempPinTitle')}
      footer={<Button onClick={() => { setCopied(false); onClose() }}>{t('users.tempPinDone')}</Button>}>
      <p>{t('users.tempPinBody', { name: value?.name ?? '' })}</p>
      <p className="amount text-center text-4xl font-bold tracking-[0.3em]" aria-label={t('users.tempPinAria', { pin: value?.pin.split('').join(' ') ?? '' })}>
        {value?.pin}
      </p>
      <Button variant="secondary" onClick={() => void navigator.clipboard?.writeText(value?.pin ?? '').then(() => setCopied(true))}>
        <Copy aria-hidden className="size-4" />{copied ? t('users.copied') : t('users.copy')}
      </Button>
      <p className="text-sm text-text-muted">{t('users.tempPinWarning')}</p>
    </Dialog>
  )
}

function SessionsPanel({ userId }: { userId: string }) {
  const { t } = useTranslation()
  const queryClient = useQueryClient()
  const sessions = useQuery({
    queryKey: ['user-sessions', userId],
    queryFn: async () => unwrap(await api.GET('/api/v1/users/{id}/sessions', { params: { path: { id: userId } } })),
  })
  const revoke = useMutation({
    mutationFn: async (familyId: string | null) =>
      unwrap(await api.POST('/api/v1/users/{id}/sessions/revoke', { params: { path: { id: userId } }, body: { familyId } })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['user-sessions', userId] }),
  })

  return (
    <div className="grid gap-2">
      <div className="flex items-center gap-2">
        <h3 className="font-medium">{t('users.sessions')}</h3>
        {(sessions.data?.length ?? 0) > 1 && (
          <Button variant="ghost" className="ml-auto" onClick={() => revoke.mutate(null)}>{t('users.signOutAll')}</Button>
        )}
      </div>
      {sessions.data?.length === 0 && <p className="text-sm text-text-muted">{t('users.noSessions')}</p>}
      <ul className="grid gap-2">
        {sessions.data?.map((s) => (
          <li key={s.familyId} className="flex flex-wrap items-center gap-2 rounded-md border border-border px-3 py-2 text-sm">
            <span className="font-medium">{s.device ?? t('users.unknownDevice')}</span>
            {s.isCurrent && <Badge tone="primary">{t('users.thisDevice')}</Badge>}
            <span className="text-text-muted">{s.lastUsedAt ? t('users.lastActive', { when: formatDateTime(s.lastUsedAt) }) : ''}</span>
            <Button variant="ghost" className="ml-auto" onClick={() => revoke.mutate(s.familyId)}>
              <LogOut aria-hidden className="size-4" />{t('users.signOut')}
            </Button>
          </li>
        ))}
      </ul>
    </div>
  )
}
