import { useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router'
import { useTranslation } from 'react-i18next'
import { useQueryClient } from '@tanstack/react-query'
import { KeyRound } from 'lucide-react'
import { Button, ErrorBanner, PinField } from '../../components/ui'
import { changePin, logout, useSession } from '../../lib/auth/session'
import { ApiError } from '../../lib/api/errors'

/**
 * S02 — choose your own PIN. Shown after the first sign-in and after an Admin reset
 * (App Flow §3.2). The session is restricted until this succeeds.
 */
export function SetPinPage() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const user = useSession((s) => s.user)
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [confirm, setConfirm] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const mismatch = confirm.length === 6 && next !== confirm
  const canSubmit = /^\d{6}$/.test(current) && /^\d{6}$/.test(next) && next === confirm

  async function submit(e: FormEvent) {
    e.preventDefault()
    if (!canSubmit) return
    setBusy(true)
    setError(null)
    try {
      await changePin(current, next)
      await queryClient.invalidateQueries({ queryKey: ['me'] })
      void navigate('/', { replace: true })
    } catch (err) {
      setError(err instanceof ApiError ? err.message : t('errors.generic'))
    } finally {
      setBusy(false)
    }
  }

  const digitsOnly = (v: string) => v.replace(/\D/g, '').slice(0, 6)

  return (
    <main className="flex min-h-dvh items-center justify-center bg-bg p-4">
      <form onSubmit={(e) => void submit(e)} className="grid w-full max-w-sm gap-5 rounded-lg border border-border bg-surface p-6" noValidate>
        <div className="grid justify-items-center gap-2 text-center">
          <KeyRound aria-hidden className="size-10 text-primary" />
          <h1 className="text-2xl font-semibold">{t('auth.setPinTitle')}</h1>
          <p className="text-sm text-text-muted">{t('auth.setPinIntro', { name: user?.fullName ?? '' })}</p>
        </div>

        <ErrorBanner message={error} />

        <PinField label={t('auth.temporaryPin')} autoComplete="current-password" value={current} onChange={(e) => setCurrent(digitsOnly(e.target.value))} />
        <PinField label={t('auth.newPin')} autoComplete="new-password" value={next} onChange={(e) => setNext(digitsOnly(e.target.value))}
          hint={t('auth.pinRules')} />
        <PinField label={t('auth.confirmPin')} autoComplete="new-password" value={confirm} onChange={(e) => setConfirm(digitsOnly(e.target.value))}
          error={mismatch ? t('auth.pinMismatch') : undefined} />

        <Button type="submit" size="lg" loading={busy} disabled={!canSubmit}>{t('auth.savePin')}</Button>
        <Button variant="ghost" onClick={() => void logout().then(() => navigate('/login', { replace: true }))}>{t('auth.signOut')}</Button>
      </form>
    </main>
  )
}
