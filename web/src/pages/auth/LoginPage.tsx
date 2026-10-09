import { useState, type FormEvent } from 'react'
import { useNavigate, useSearchParams } from 'react-router'
import { useTranslation } from 'react-i18next'
import { Building2 } from 'lucide-react'
import { Button, Dialog, ErrorBanner, Field, PinField } from '../../components/ui'
import { login } from '../../lib/auth/session'
import { ApiError } from '../../lib/api/errors'

const MOBILE_RE = /^[6-9]\d{9}$/

/** S01 — mobile + PIN sign-in (App Flow §3.1, Design Brief §6.1). */
export function LoginPage() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const [params] = useSearchParams()
  const [mobile, setMobile] = useState('')
  const [pin, setPin] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [forgotOpen, setForgotOpen] = useState(false)

  const digits = mobile.replace(/\D/g, '').slice(-10)
  const canSubmit = MOBILE_RE.test(digits) && /^\d{6}$/.test(pin)

  async function submit(e: FormEvent) {
    e.preventDefault()
    if (!canSubmit) return
    setBusy(true)
    setError(null)
    try {
      const user = await login(digits, pin)
      const next = params.get('next')
      void navigate(user.pinMustChange ? '/login/set-pin' : next && next.startsWith('/') ? next : '/', { replace: true })
    } catch (err) {
      setPin('')
      if (err instanceof ApiError && err.code === 'ACCOUNT_LOCKED' && err.retryAfterSeconds) {
        setError(t('auth.lockedFor', { minutes: Math.ceil(err.retryAfterSeconds / 60) }))
      } else {
        setError(err instanceof ApiError ? err.message : t('errors.generic'))
      }
    } finally {
      setBusy(false)
    }
  }

  return (
    <main className="flex min-h-dvh items-center justify-center bg-bg p-4">
      <form onSubmit={(e) => void submit(e)} className="grid w-full max-w-sm gap-5 rounded-lg border border-border bg-surface p-6" noValidate>
        <div className="grid justify-items-center gap-2 text-center">
          <Building2 aria-hidden className="size-10 text-primary" />
          <p className="text-lg font-semibold">Fund<span className="font-bold">Ledger</span></p>
          <h1 className="text-2xl font-semibold">{t('auth.signIn')}</h1>
        </div>

        <ErrorBanner message={error} />

        <Field label={t('auth.mobile')} prefix="+91" inputMode="numeric" autoComplete="username" required
          value={mobile} onChange={(e) => setMobile(e.target.value)} placeholder="98765 43210" />
        <PinField label={t('auth.pin')} autoComplete="current-password" value={pin}
          onChange={(e) => setPin(e.target.value.replace(/\D/g, '').slice(0, 6))} />

        <Button type="submit" size="lg" loading={busy} disabled={!canSubmit}>{t('auth.signInButton')}</Button>
        <button type="button" onClick={() => setForgotOpen(true)} className="min-h-11 text-sm font-medium text-primary underline-offset-4 hover:underline">
          {t('auth.forgotPin')}
        </button>
        <p className="text-center text-xs text-text-muted">{t('auth.onlyRegistered')}</p>
      </form>

      <Dialog open={forgotOpen} onClose={() => setForgotOpen(false)} title={t('auth.forgotPin')}
        footer={<Button onClick={() => setForgotOpen(false)}>{t('actions.ok')}</Button>}>
        <p>{t('auth.forgotPinBody')}</p>
      </Dialog>
    </main>
  )
}
