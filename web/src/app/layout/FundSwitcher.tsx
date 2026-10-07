import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Check, ChevronDown } from 'lucide-react'
import { useFundSelection, useMe, useSelectedFund } from '../../lib/auth/queries'
import { setOrganizationTimeZone } from '../../lib/format/date'
import { Badge } from '../../components/ui'

/** S05 — the global fund context (App Flow N-1..N-5). */
export function FundSwitcher() {
  const { t } = useTranslation()
  const me = useMe()
  const selected = useSelectedFund(me.data)
  const select = useFundSelection((s) => s.select)
  const [open, setOpen] = useState(false)
  const ref = useRef<HTMLDivElement>(null)

  useEffect(() => setOrganizationTimeZone(me.data?.organization.timezone), [me.data?.organization.timezone])

  useEffect(() => {
    if (!open) return
    const onDoc = (e: MouseEvent) => { if (!ref.current?.contains(e.target as Node)) setOpen(false) }
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false) }
    document.addEventListener('mousedown', onDoc)
    document.addEventListener('keydown', onKey)
    return () => { document.removeEventListener('mousedown', onDoc); document.removeEventListener('keydown', onKey) }
  }, [open])

  const funds = me.data?.funds ?? []
  const label = selected?.name ?? (me.isPending ? '…' : t('fund.noneSelected'))

  if (funds.length <= 1) {
    // N-2: a single fund is shown as a plain label.
    return <span className="flex min-h-11 items-center px-2 font-semibold">{label}</span>
  }

  return (
    <div ref={ref} className="relative">
      <button type="button" onClick={() => setOpen((o) => !o)} aria-haspopup="listbox" aria-expanded={open}
        aria-label={t('fund.switch')} className="flex min-h-11 items-center gap-1 rounded-md px-2 font-semibold hover:bg-surface-muted">
        {label} <ChevronDown aria-hidden className="size-4" />
      </button>
      {open && (
        <ul role="listbox" aria-label={t('fund.switch')}
          className="absolute left-0 top-12 z-30 grid min-w-64 gap-1 rounded-md border border-border bg-surface p-1 shadow-lg">
          {funds.map((f) => (
            <li key={f.id} role="option" aria-selected={f.id === selected?.id}>
              <button type="button" onClick={() => { select(me.data!.id, f.id); setOpen(false) }}
                className="flex min-h-11 w-full items-center gap-2 rounded-md px-3 text-left hover:bg-surface-muted">
                <span className="w-4">{f.id === selected?.id && <Check aria-hidden className="size-4 text-primary" />}</span>
                <span className="flex-1">{f.name}</span>
                {f.status !== 'ACTIVE' && <Badge>{t(`funds.status.${f.status}`)}</Badge>}
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
