import { useEffect, type ReactNode } from 'react'
import { Navigate, useLocation } from 'react-router'
import { useTranslation } from 'react-i18next'
import { Loader2 } from 'lucide-react'
import { refresh, useSession } from '../lib/auth/session'
import { NotFoundPage } from '../pages/NotFoundPage'

/** Restores the session from the refresh cookie once per page load. */
export function SessionBootstrap({ children }: { children: ReactNode }) {
  const status = useSession((s) => s.status)
  useEffect(() => {
    if (status === 'unknown') void refresh()
  }, [status])
  return <>{children}</>
}

function Splash() {
  const { t } = useTranslation()
  return (
    <div role="status" className="flex min-h-dvh items-center justify-center bg-bg text-text-muted">
      <Loader2 aria-hidden className="mr-2 size-5 animate-spin" />
      {t('app.loading')}
    </div>
  )
}

/**
 * Signed-in routes. Anonymous → /login?next=…; temporary PIN → /login/set-pin
 * (the API enforces the same restriction; this only avoids dead-end screens).
 */
export function RequireAuth({ children, allowPinChange = false }: { children: ReactNode; allowPinChange?: boolean }) {
  const status = useSession((s) => s.status)
  const user = useSession((s) => s.user)
  const location = useLocation()

  if (status === 'unknown') return <Splash />
  if (status === 'anonymous' || !user) {
    const next = encodeURIComponent(location.pathname + location.search)
    return <Navigate to={`/login?next=${next}`} replace />
  }

  if (user.pinMustChange && !allowPinChange) return <Navigate to="/login/set-pin" replace />
  return <>{children}</>
}

/** Admin-only routes show the generic not-found page to Members (App Flow N-8). */
export function RequireAdmin({ children }: { children: ReactNode }) {
  const role = useSession((s) => s.user?.role)
  return role === 'ADMIN' ? <>{children}</> : <NotFoundPage />
}

/** /login while already signed in goes home. */
export function RedirectIfSignedIn({ children }: { children: ReactNode }) {
  const status = useSession((s) => s.status)
  const user = useSession((s) => s.user)
  if (status === 'unknown') return <Splash />
  if (status === 'authenticated' && user) return <Navigate to={user.pinMustChange ? '/login/set-pin' : '/'} replace />
  return <>{children}</>
}
