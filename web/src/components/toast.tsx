import { useEffect } from 'react'
import { useToasts, type Toast } from '../lib/toast'
import { CheckCircle2, X, XCircle } from 'lucide-react'

/** Polite live region; success toasts disappear after 4 s, errors stay until dismissed (App Flow §7). */
export function Toaster() {
  const toasts = useToasts((s) => s.toasts)
  const dismiss = useToasts((s) => s.dismiss)
  return (
    <div aria-live="polite" className="pointer-events-none fixed inset-x-0 bottom-24 z-50 flex flex-col items-center gap-2 px-4 lg:bottom-6">
      {toasts.map((t) => <ToastItem key={t.id} toast={t} onDismiss={() => dismiss(t.id)} />)}
    </div>
  )
}

function ToastItem({ toast: t, onDismiss }: { toast: Toast; onDismiss: () => void }) {
  useEffect(() => {
    if (t.tone !== 'success') return
    const timer = setTimeout(onDismiss, 4000)
    return () => clearTimeout(timer)
  }, [t.tone, onDismiss])
  const Icon = t.tone === 'success' ? CheckCircle2 : XCircle
  return (
    <div role="status" className={`pointer-events-auto flex max-w-md items-center gap-2 rounded-md border bg-surface px-3 py-2 text-sm shadow-lg ${t.tone === 'success' ? 'border-success' : 'border-danger'}`}>
      <Icon aria-hidden className={`size-5 shrink-0 ${t.tone === 'success' ? 'text-success' : 'text-danger'}`} />
      <span>{t.message}</span>
      <button type="button" onClick={onDismiss} aria-label="Dismiss" className="ml-1 flex size-8 items-center justify-center rounded-md hover:bg-surface-muted">
        <X aria-hidden className="size-4" />
      </button>
    </div>
  )
}
