import { forwardRef, useId, type ReactNode } from 'react'
import { groupWhileTyping, sanitizeAmount, formatSigned, type TxnType } from '../../lib/format/money'
import { ArrowDownLeft, ArrowRightLeft, ArrowUpRight, Scale } from 'lucide-react'
import { useTranslation } from 'react-i18next'

// Money-specific primitives (Design Brief §5). The type colour is ALWAYS paired with a sign/glyph,
// an icon and a text label (WCAG 1.4.1), never colour alone.

type AmountInputProps = {
  label: string
  value: string // raw "125000.5" (no grouping)
  onChange: (raw: string) => void
  error?: string | undefined
  accent?: 'in' | 'out' | 'transfer' | 'adjust' | 'none'
  autoFocus?: boolean
}

const accents = { in: 'border-in', out: 'border-out', transfer: 'border-transfer', adjust: 'border-adjust', none: 'border-border' } as const

/** Large ₹ amount field with live Indian grouping; numeric keypad on phones. */
export const AmountInput = forwardRef<HTMLInputElement, AmountInputProps>(function AmountInput(
  { label, value, onChange, error, accent = 'none', autoFocus },
  ref,
) {
  const id = useId()
  return (
    <div className="grid gap-1">
      <label htmlFor={id} className="text-sm font-medium">{label}<span aria-hidden className="text-danger"> *</span></label>
      <div className={`flex items-center gap-2 border-b-4 bg-surface px-1 ${error ? 'border-danger' : accents[accent]}`}>
        <span aria-hidden className="text-3xl text-text-muted">₹</span>
        <input
          ref={ref}
          id={id}
          inputMode="decimal"
          autoComplete="off"
          autoFocus={autoFocus}
          placeholder="0"
          value={groupWhileTyping(value)}
          onChange={(e) => onChange(sanitizeAmount(e.target.value))}
          aria-invalid={error ? true : undefined}
          aria-describedby={error ? `${id}-error` : undefined}
          className="amount min-h-14 w-full bg-transparent text-[40px] font-bold leading-[48px] outline-none"
        />
      </div>
      {error && <p id={`${id}-error`} role="alert" className="text-sm text-danger">{error}</p>}
    </div>
  )
})

type ChipOption = { id: string; label: string; hint?: string }

/** Single-select chips (category, account, payment mode). Radio semantics, ≥ 44px targets. */
export function ChipGroup({ label, options, value, onChange, error, required, moreLabel }: {
  label: string
  options: ChipOption[]
  value: string
  onChange: (id: string) => void
  error?: string | undefined
  required?: boolean
  moreLabel?: string
}) {
  const id = useId()
  const MAX = 6
  const selectedIndex = options.findIndex((o) => o.id === value)
  // Keep the selected option visible even when it is beyond the first chips.
  const visible = options.slice(0, MAX)
  if (selectedIndex >= MAX && options[selectedIndex]) visible[MAX - 1] = options[selectedIndex]
  const rest = options.filter((o) => !visible.some((v) => v.id === o.id))

  return (
    <div className="grid gap-1">
      <span id={`${id}-label`} className="text-sm font-medium">{label}{required && <span aria-hidden className="text-danger"> *</span>}</span>
      <div role="radiogroup" aria-labelledby={`${id}-label`} className="flex flex-wrap gap-2">
        {visible.map((o) => (
          <button
            key={o.id}
            type="button"
            role="radio"
            aria-checked={o.id === value}
            onClick={() => onChange(o.id)}
            className={`min-h-11 rounded-md border px-3 text-sm ${o.id === value ? 'border-primary bg-primary font-semibold text-primary-fg' : 'border-border bg-surface hover:bg-surface-muted'}`}
          >
            {o.label}
            {o.hint && <span className={`amount ml-1.5 text-xs ${o.id === value ? 'opacity-90' : 'text-text-muted'}`}>{o.hint}</span>}
          </button>
        ))}
        {rest.length > 0 && (
          <select
            aria-label={moreLabel}
            value=""
            onChange={(e) => e.target.value && onChange(e.target.value)}
            className="min-h-11 rounded-md border border-border bg-surface px-2 text-sm"
          >
            <option value="">{moreLabel}</option>
            {rest.map((o) => <option key={o.id} value={o.id}>{o.label}</option>)}
          </select>
        )}
      </div>
      {error && <p role="alert" className="text-sm text-danger">{error}</p>}
    </div>
  )
}

const TYPE_STYLE: Record<TxnType, { icon: typeof ArrowDownLeft; tone: string; bg: string; key: string }> = {
  DEPOSIT: { icon: ArrowDownLeft, tone: 'text-in', bg: 'bg-in-bg', key: 'txn.type.DEPOSIT' },
  EXPENSE: { icon: ArrowUpRight, tone: 'text-out', bg: 'bg-out-bg', key: 'txn.type.EXPENSE' },
  TRANSFER: { icon: ArrowRightLeft, tone: 'text-transfer', bg: 'bg-transfer-bg', key: 'txn.type.TRANSFER' },
  ADJUSTMENT: { icon: Scale, tone: 'text-adjust', bg: 'bg-adjust-bg', key: 'txn.type.ADJUSTMENT' },
}

export function TxnTypeBadge({ type }: { type: TxnType }) {
  const { t } = useTranslation()
  const s = TYPE_STYLE[type]
  const Icon = s.icon
  return (
    <span className={`inline-flex items-center gap-1 rounded-sm px-2 py-0.5 text-xs font-medium ${s.bg} ${s.tone}`}>
      <Icon aria-hidden className="size-3.5" />{t(s.key)}
    </span>
  )
}

/** "+ ₹25,000" in the type colour, with a text equivalent for screen readers. */
export function SignedAmount({ type, amount, adjustment, cancelled, className = '' }: {
  type: TxnType; amount: string; adjustment?: 'INCREASE' | 'DECREASE' | null; cancelled?: boolean; className?: string
}) {
  const { t } = useTranslation()
  const tone = cancelled ? 'text-cancelled line-through' : TYPE_STYLE[type].tone
  return (
    <span className={`amount font-semibold ${tone} ${className}`}>
      <span aria-hidden>{formatSigned(type, amount, adjustment ?? undefined)}</span>
      <span className="sr-only">{t(TYPE_STYLE[type].key)}, {amount} rupees</span>
    </span>
  )
}

export function Section({ title, children, id }: { title: string; children: ReactNode; id?: string }) {
  return (
    <section aria-labelledby={id} className="grid gap-3 rounded-lg border border-border bg-surface p-4">
      <h2 id={id} className="font-semibold">{title}</h2>
      {children}
    </section>
  )
}
