import { useEffect, useRef } from 'react'
import { useNavigate } from 'react-router'
import { useTranslation } from 'react-i18next'
import { ArrowDownLeft, ArrowRightLeft, ArrowUpRight, X } from 'lucide-react'

// Mobile (＋) sheet: Money In / Money Out / Transfer (App Flow §2.1).
// Permission filtering per fund arrives with Phase 1–2; all three show for now.
export function QuickActionSheet({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const dialogRef = useRef<HTMLDialogElement>(null)

  useEffect(() => {
    const dialog = dialogRef.current
    if (!dialog) return
    if (open && !dialog.open) dialog.showModal?.()
    if (!open && dialog.open) dialog.close?.()
  }, [open])

  const go = (to: string) => {
    onClose()
    void navigate(to)
  }

  const actions = [
    { to: '/new/in', label: t('actions.moneyIn'), icon: ArrowDownLeft, tone: 'bg-in-bg text-in', glyph: '+' },
    { to: '/new/out', label: t('actions.moneyOut'), icon: ArrowUpRight, tone: 'bg-out-bg text-out', glyph: '−' },
    { to: '/new/transfer', label: t('actions.transfer'), icon: ArrowRightLeft, tone: 'bg-transfer-bg text-transfer', glyph: '⇄' },
  ]

  return (
    <dialog
      ref={dialogRef}
      onClose={onClose}
      aria-label={t('nav.add')}
      className="m-0 mt-auto w-full max-w-none rounded-t-lg bg-surface p-4 text-text backdrop:bg-black/40 lg:hidden"
    >
      <div className="mb-3 flex items-center justify-between">
        <h2 className="text-lg font-semibold">{t('nav.add')}</h2>
        <button type="button" onClick={onClose} className="flex size-11 items-center justify-center rounded-md hover:bg-surface-muted" aria-label={t('actions.close')}>
          <X aria-hidden className="size-5" />
        </button>
      </div>
      <ul className="grid gap-2">
        {actions.map(({ to, label, icon: Icon, tone, glyph }) => (
          <li key={to}>
            <button type="button" onClick={() => go(to)} className="flex min-h-14 w-full items-center gap-3 rounded-md border border-border px-3 text-left font-medium hover:bg-surface-muted">
              <span className={`flex size-9 items-center justify-center rounded-full ${tone}`}>
                <Icon aria-hidden className="size-5" />
              </span>
              <span aria-hidden className="w-4 text-center">{glyph}</span>
              {label}
            </button>
          </li>
        ))}
      </ul>
    </dialog>
  )
}
