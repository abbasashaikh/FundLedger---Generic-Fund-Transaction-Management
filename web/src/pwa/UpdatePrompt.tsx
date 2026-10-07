import { useRegisterSW } from 'virtual:pwa-register/react'
import { useTranslation } from 'react-i18next'

// Service-worker update banner. Never reloads on its own: the user decides, so an
// in-progress transaction form is never lost (Design Brief §10, App Flow §7).
export function UpdatePrompt() {
  const { t } = useTranslation()
  const {
    needRefresh: [needRefresh],
    updateServiceWorker,
  } = useRegisterSW()

  if (!needRefresh) return null

  return (
    <div role="status" className="flex items-center gap-3 border-b border-border bg-surface-muted px-4 py-2 text-sm">
      <span>{t('pwa.updateAvailable')}</span>
      <button type="button" onClick={() => void updateServiceWorker(true)} className="ml-auto min-h-11 rounded-md bg-primary px-3 font-medium text-primary-fg">
        {t('pwa.refresh')}
      </button>
    </div>
  )
}
