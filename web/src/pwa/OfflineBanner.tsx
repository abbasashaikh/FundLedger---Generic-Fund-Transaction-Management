import { useTranslation } from 'react-i18next'
import { CloudOff } from 'lucide-react'
import { useOnline } from './useOnline'

/** Persistent, subtle banner while offline (App Flow §4.8). */
export function OfflineBanner() {
  const { t } = useTranslation()
  if (useOnline()) return null
  return (
    <div role="status" className="flex items-center gap-2 border-b border-border bg-surface-muted px-4 py-2 text-sm text-text-muted">
      <CloudOff aria-hidden className="size-4" />
      {t('pwa.offline')}
    </div>
  )
}
