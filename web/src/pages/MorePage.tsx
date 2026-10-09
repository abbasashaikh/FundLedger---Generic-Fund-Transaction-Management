import { useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { KeyRound, LogOut } from 'lucide-react'
import { api } from '../lib/api/client'
import { ApiError } from '../lib/api/errors'
import { readThemePreference, saveThemePreference, type ThemePreference } from '../lib/theme'
import { changePin, logout, useSession } from '../lib/auth/session'
import { useMe } from '../lib/auth/queries'
import { Button, Dialog, ErrorBanner, PinField } from '../components/ui'

// More (S17): profile, theme, change PIN, sign out, admin links on mobile, system info.
export function MorePage() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const user = useSession((s) => s.user)
  const me = useMe()
  const [theme, setTheme] = useState<ThemePreference>(readThemePreference)
  const [pinOpen, setPinOpen] = useState(false)
  const [confirmLogout, setConfirmLogout] = useState(false)

  const version = useQuery({
    queryKey: ['system', 'version'],
    queryFn: async () => {
      const { data, error } = await api.GET('/api/v1/version')
      if (error || !data) throw new Error('unreachable')
      return data
    },
  })

  const choose = (pref: ThemePreference) => {
    setTheme(pref)
    saveThemePreference(pref)
  }

  const signOut = async () => {
    await logout()
    queryClient.clear()
    void navigate('/login', { replace: true })
  }

  const admin = [
    ['/admin/users', 'nav.users'], ['/admin/funds', 'nav.funds'], ['/admin/accounts', 'nav.accounts'],
    ['/admin/categories', 'nav.categories'], ['/admin/lookups', 'nav.lookups'], ['/admin/audit', 'nav.audit'],
    ['/admin/settings', 'nav.settings'],
  ] as const

  return (
    <div className="mx-auto grid max-w-xl gap-6">
      <h1 className="text-2xl font-semibold">{t('screens.more')}</h1>

      <section className="grid gap-3 rounded-lg border border-border bg-surface p-4" aria-labelledby="profile-heading">
        <h2 id="profile-heading" className="font-semibold">{user?.fullName}</h2>
        <p className="text-sm text-text-muted">
          {t(user?.role === 'ADMIN' ? 'users.roleAdmin' : 'users.roleMember')}
          {me.data && ` · ${me.data.organization.name}`}
        </p>
        <div className="flex flex-wrap gap-2">
          <Button variant="secondary" onClick={() => setPinOpen(true)}><KeyRound aria-hidden className="size-4" />{t('auth.changePin')}</Button>
          <Button variant="secondary" onClick={() => setConfirmLogout(true)}><LogOut aria-hidden className="size-4" />{t('auth.signOut')}</Button>
        </div>
      </section>

      <fieldset className="rounded-lg border border-border bg-surface p-4">
        <legend className="px-1 font-semibold">{t('theme.label')}</legend>
        <div className="mt-2 grid grid-cols-3 gap-2" role="radiogroup">
          {(['system', 'light', 'dark'] as const).map((pref) => (
            <label key={pref} className={`flex min-h-11 cursor-pointer items-center justify-center rounded-md border text-sm ${theme === pref ? 'border-primary font-semibold text-primary' : 'border-border'}`}>
              <input type="radio" name="theme" value={pref} checked={theme === pref} onChange={() => choose(pref)} className="sr-only" />
              {t(`theme.${pref}`)}
            </label>
          ))}
        </div>
      </fieldset>

      {user?.role === 'ADMIN' && (
        <section className="rounded-lg border border-border bg-surface p-4 lg:hidden" aria-labelledby="admin-heading">
          <h2 id="admin-heading" className="font-semibold">{t('nav.administration')}</h2>
          <ul className="mt-2 grid">
            {admin.map(([to, key]) => (
              <li key={to}>
                <Link to={to} className="flex min-h-11 items-center rounded-md px-2 hover:bg-surface-muted">{t(key)}</Link>
              </li>
            ))}
          </ul>
        </section>
      )}

      <section className="rounded-lg border border-border bg-surface p-4" aria-labelledby="system-heading">
        <h2 id="system-heading" className="font-semibold">{t('system.title')}</h2>
        {version.isPending && <p className="mt-2 text-text-muted">{t('system.checking')}</p>}
        {version.isError && <p className="mt-2 text-danger">{t('system.apiUnreachable')}</p>}
        {version.data && (
          <dl className="mt-2 grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm">
            <dt className="text-text-muted">{t('system.apiVersion')}</dt>
            <dd>{version.data.version} ({version.data.commit.slice(0, 7)})</dd>
            <dt className="text-text-muted">{t('system.apiEnvironment')}</dt>
            <dd>{version.data.environment}</dd>
          </dl>
        )}
      </section>

      <ChangePinDialog open={pinOpen} onClose={() => setPinOpen(false)} />

      <Dialog open={confirmLogout} onClose={() => setConfirmLogout(false)} title={t('auth.signOutTitle')}
        footer={<>
          <Button variant="secondary" onClick={() => setConfirmLogout(false)}>{t('actions.cancel')}</Button>
          <Button onClick={() => void signOut()}>{t('auth.signOut')}</Button>
        </>}>
        <p>{t('auth.signOutBody')}</p>
      </Dialog>
    </div>
  )
}

function ChangePinDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation()
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [confirm, setConfirm] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [done, setDone] = useState(false)
  const digits = (v: string) => v.replace(/\D/g, '').slice(0, 6)
  const canSubmit = /^\d{6}$/.test(current) && /^\d{6}$/.test(next) && next === confirm

  const close = () => {
    setCurrent(''); setNext(''); setConfirm(''); setError(null); setDone(false)
    onClose()
  }

  const submit = async () => {
    setBusy(true)
    setError(null)
    try {
      await changePin(current, next)
      setDone(true)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : t('errors.generic'))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Dialog open={open} onClose={close} title={t('auth.changePin')}
      footer={done
        ? <Button onClick={close}>{t('actions.ok')}</Button>
        : <>
            <Button variant="secondary" onClick={close}>{t('actions.cancel')}</Button>
            <Button loading={busy} disabled={!canSubmit} onClick={() => void submit()}>{t('auth.savePin')}</Button>
          </>}>
      {done ? <p role="status">{t('auth.pinChanged')}</p> : (
        <>
          <ErrorBanner message={error} />
          <PinField label={t('auth.currentPin')} autoComplete="current-password" value={current} onChange={(e) => setCurrent(digits(e.target.value))} />
          <PinField label={t('auth.newPin')} autoComplete="new-password" value={next} onChange={(e) => setNext(digits(e.target.value))} hint={t('auth.pinRules')} />
          <PinField label={t('auth.confirmPin')} autoComplete="new-password" value={confirm} onChange={(e) => setConfirm(digits(e.target.value))}
            error={confirm.length === 6 && confirm !== next ? t('auth.pinMismatch') : undefined} />
        </>
      )}
    </Dialog>
  )
}
