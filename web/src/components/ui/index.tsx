import { forwardRef, useEffect, useId, useRef, useState, type ButtonHTMLAttributes, type InputHTMLAttributes, type ReactNode } from 'react'
import { Eye, EyeOff, Loader2, X } from 'lucide-react'
import { useTranslation } from 'react-i18next'

// Design-system primitives (Design Brief §5). Touch targets ≥ 44px, visible labels,
// errors linked via aria-describedby (§8).

type ButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: 'primary' | 'secondary' | 'ghost' | 'destructive'
  size?: 'md' | 'lg'
  loading?: boolean
}

const variants: Record<NonNullable<ButtonProps['variant']>, string> = {
  primary: 'bg-primary text-primary-fg hover:opacity-90',
  secondary: 'border border-border bg-surface text-text hover:bg-surface-muted',
  ghost: 'text-text hover:bg-surface-muted',
  destructive: 'bg-danger text-white hover:opacity-90',
}

export function Button({ variant = 'primary', size = 'md', loading, disabled, children, className = '', ...rest }: ButtonProps) {
  return (
    <button
      type="button"
      {...rest}
      disabled={disabled || loading}
      aria-busy={loading || undefined}
      className={`inline-flex items-center justify-center gap-2 rounded-md px-4 font-medium transition disabled:cursor-not-allowed disabled:opacity-50 ${
        size === 'lg' ? 'min-h-13 text-base' : 'min-h-11 text-sm'
      } ${variants[variant]} ${className}`}
    >
      {loading && <Loader2 aria-hidden className="size-4 animate-spin" />}
      {children}
    </button>
  )
}

type FieldProps = InputHTMLAttributes<HTMLInputElement> & {
  label: string
  error?: string | undefined
  hint?: string | undefined
  prefix?: string
}

export const Field = forwardRef<HTMLInputElement, FieldProps>(function Field(
  { label, error, hint, prefix, id, className = '', ...rest },
  ref,
) {
  const autoId = useId()
  const inputId = id ?? autoId
  const describedBy = [error ? `${inputId}-error` : null, hint ? `${inputId}-hint` : null].filter(Boolean).join(' ') || undefined
  return (
    <div className={`grid gap-1 ${className}`}>
      <label htmlFor={inputId} className="text-sm font-medium">
        {label}
        {rest.required && <span aria-hidden className="text-danger"> *</span>}
      </label>
      <div className={`flex min-h-11 items-center rounded-md border bg-surface ${error ? 'border-danger' : 'border-border'} focus-within:outline-2 focus-within:outline-primary`}>
        {prefix && <span className="border-r border-border px-3 text-text-muted">{prefix}</span>}
        <input
          ref={ref}
          id={inputId}
          aria-invalid={error ? true : undefined}
          aria-describedby={describedBy}
          className="min-h-11 w-full rounded-md bg-transparent px-3 outline-none"
          {...rest}
        />
      </div>
      {hint && !error && <p id={`${inputId}-hint`} className="text-xs text-text-muted">{hint}</p>}
      {error && <p id={`${inputId}-error`} role="alert" className="text-sm text-danger">{error}</p>}
    </div>
  )
})

/** 6-digit masked PIN with a show/hide toggle; fillable by password managers (Design Brief §6.1). */
export const PinField = forwardRef<HTMLInputElement, Omit<FieldProps, 'type' | 'inputMode' | 'maxLength'> & { autoComplete: 'current-password' | 'new-password' }>(
  function PinField({ label, error, hint, autoComplete, ...rest }, ref) {
    const { t } = useTranslation()
    const [visible, setVisible] = useState(false)
    const id = useId()
    return (
      <div className="grid gap-1">
        <label htmlFor={id} className="text-sm font-medium">{label}<span aria-hidden className="text-danger"> *</span></label>
        <div className={`flex min-h-11 items-center rounded-md border bg-surface ${error ? 'border-danger' : 'border-border'} focus-within:outline-2 focus-within:outline-primary`}>
          <input
            ref={ref}
            id={id}
            type={visible ? 'text' : 'password'}
            inputMode="numeric"
            pattern="[0-9]{6}"
            maxLength={6}
            autoComplete={autoComplete}
            required
            aria-invalid={error ? true : undefined}
            aria-describedby={error ? `${id}-error` : hint ? `${id}-hint` : undefined}
            className="amount min-h-11 w-full rounded-md bg-transparent px-3 tracking-[0.4em] outline-none"
            {...rest}
          />
          <button type="button" onClick={() => setVisible((v) => !v)} className="flex size-11 items-center justify-center text-text-muted"
            aria-label={visible ? t('auth.hidePin') : t('auth.showPin')} aria-pressed={visible}>
            {visible ? <EyeOff aria-hidden className="size-5" /> : <Eye aria-hidden className="size-5" />}
          </button>
        </div>
        {hint && !error && <p id={`${id}-hint`} className="text-xs text-text-muted">{hint}</p>}
        {error && <p id={`${id}-error`} role="alert" className="text-sm text-danger">{error}</p>}
      </div>
    )
  },
)

export function Checkbox({ label, checked, onChange, disabled }: { label: string; checked: boolean; onChange: (v: boolean) => void; disabled?: boolean }) {
  return (
    <label className={`flex min-h-11 cursor-pointer items-center gap-2 text-sm ${disabled ? 'opacity-50' : ''}`}>
      <input type="checkbox" className="size-5 accent-[var(--color-primary)]" checked={checked} disabled={disabled}
        onChange={(e) => onChange(e.target.checked)} />
      {label}
    </label>
  )
}

/** Modal dialog on <dialog>: focus trap, Esc to close, focus returns to the trigger. */
export function Dialog({ open, onClose, title, children, footer }: {
  open: boolean; onClose: () => void; title: string; children: ReactNode; footer?: ReactNode
}) {
  const { t } = useTranslation()
  const ref = useRef<HTMLDialogElement>(null)
  useEffect(() => {
    const d = ref.current
    if (!d) return
    if (open && !d.open) d.showModal?.()
    if (!open && d.open) d.close?.()
  }, [open])
  return (
    <dialog ref={ref} onClose={onClose} aria-labelledby="dialog-title"
      className="m-auto w-[min(32rem,calc(100%-2rem))] rounded-lg bg-surface p-0 text-text backdrop:bg-black/40">
      <div className="flex items-center justify-between border-b border-border px-4 py-3">
        <h2 id="dialog-title" className="text-lg font-semibold">{title}</h2>
        <button type="button" onClick={onClose} aria-label={t('actions.close')} className="flex size-11 items-center justify-center rounded-md hover:bg-surface-muted">
          <X aria-hidden className="size-5" />
        </button>
      </div>
      <div className="grid gap-4 p-4">{children}</div>
      {footer && <div className="flex flex-wrap justify-end gap-2 border-t border-border px-4 py-3">{footer}</div>}
    </dialog>
  )
}

export function Badge({ tone = 'muted', children }: { tone?: 'muted' | 'success' | 'warning' | 'danger' | 'primary'; children: ReactNode }) {
  const tones = {
    muted: 'bg-surface-muted text-text-muted',
    success: 'bg-in-bg text-in',
    warning: 'bg-adjust-bg text-adjust',
    danger: 'bg-out-bg text-out',
    primary: 'bg-transfer-bg text-transfer',
  }
  return <span className={`inline-flex items-center rounded-sm px-2 py-0.5 text-xs font-medium ${tones[tone]}`}>{children}</span>
}

export function ErrorBanner({ message }: { message: string | null | undefined }) {
  if (!message) return null
  return <div role="alert" className="rounded-md border border-danger bg-out-bg px-3 py-2 text-sm text-out">{message}</div>
}
