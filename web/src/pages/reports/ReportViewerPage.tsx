import { useState } from 'react'
import { Link, useParams } from 'react-router'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { ArrowLeft, Download } from 'lucide-react'
import { api, type Schemas } from '../../lib/api/client'
import { ApiError, unwrap } from '../../lib/api/errors'
import { useAccounts, useCategories } from '../../lib/api/hooks'
import { useMe, useSelectedFund } from '../../lib/auth/queries'
import { addDays, formatDate, startOfMonth, startOfWeek, todayIso } from '../../lib/format/date'
import { formatRupees } from '../../lib/format/money'
import { Badge, Button, ErrorBanner, Field } from '../../components/ui'
import { NotFoundPage } from '../NotFoundPage'
import { ExportDialog } from './ExportDialog'

type Report = Schemas['ReportResult']
type Column = Schemas['ReportColumn']
type Preset = 'month' | 'lastMonth' | 'today' | 'week' | 'fy' | 'all' | 'custom'

const LIST_REPORTS = new Set(['MONEY_IN', 'MONEY_OUT'])
const ACCOUNT_FILTER = new Set(['MONEY_IN', 'MONEY_OUT', 'TRANSFER'])

/** April 1 of the financial year containing `today` (Indian FY, decision Q-08). */
function startOfFinancialYear(today: string): string {
  const year = Number(today.slice(0, 4))
  const month = Number(today.slice(5, 7))
  return `${month >= 4 ? year : year - 1}-04-01`
}

function range(preset: Preset, today: string): { from?: string; to?: string } {
  switch (preset) {
    case 'today': return { from: today, to: today }
    case 'week': return { from: startOfWeek(today), to: today }
    case 'month': return { from: startOfMonth(today), to: today }
    case 'lastMonth': {
      const end = addDays(startOfMonth(today), -1)
      return { from: startOfMonth(end), to: end }
    }
    case 'fy': return { from: startOfFinancialYear(today), to: today }
    default: return {}
  }
}

/** Shows one cell the way its column says: money in rupees, dates as dd-MMM-yyyy, the rest as text. */
function cellText(column: Column, value: string | null | undefined): string {
  if (value === null || value === undefined || value === '') return ''
  if (column.kind === 'MONEY') return formatRupees(value)
  if (column.kind === 'DATE') return formatDate(value)
  return value
}

/** S15 — one report: period, filters, summary cards, then the detail (table on desktop, cards on phones). */
export function ReportViewerPage() {
  const { t } = useTranslation()
  const { code = '' } = useParams()
  const me = useMe()
  const fund = useSelectedFund(me.data)
  const today = todayIso()
  const [preset, setPreset] = useState<Preset>('month')
  const [customFrom, setCustomFrom] = useState(startOfMonth(today))
  const [customTo, setCustomTo] = useState(today)
  const [categoryId, setCategoryId] = useState('')
  const [accountId, setAccountId] = useState('')
  const [exportOpen, setExportOpen] = useState(false)

  const period = preset === 'custom' ? { from: customFrom || undefined, to: customTo || undefined } : range(preset, today)
  const filters = { categoryId: categoryId || undefined, accountId: accountId || undefined }
  const direction = code === 'MONEY_IN' ? 'MONEY_IN' : code === 'MONEY_OUT' ? 'MONEY_OUT' : undefined
  const categories = useCategories(direction, fund?.id)
  const accounts = useAccounts()

  const report = useQuery<Report, ApiError>({
    queryKey: ['report', code, fund?.id, period, filters],
    enabled: !!fund?.permissions.viewReports,
    queryFn: async () => unwrap(await api.GET('/api/v1/reports/{code}', {
      params: { path: { code }, query: { fundId: fund!.id, ...period, ...filters } },
    })),
  })

  if (me.isPending) return <p className="text-text-muted">{t('app.loading')}</p>
  if (!fund) return <p className="py-16 text-center text-text-muted">{t('txn.noFund')}</p>
  if (!fund.permissions.viewReports) return <p className="py-16 text-center">{t('reports.noAccess')}</p>
  if (report.error?.status === 404) return <NotFoundPage />

  const r = report.data
  const presets: Preset[] = ['month', 'lastMonth', 'today', 'week', 'fy', 'all', 'custom']
  return (
    <div className="mx-auto grid max-w-5xl gap-4">
      <div className="flex flex-wrap items-center gap-2">
        <Link to="/reports" aria-label={t('screens.reports')} className="flex size-11 items-center justify-center rounded-md hover:bg-surface-muted"><ArrowLeft aria-hidden className="size-5" /></Link>
        <div>
          <h1 className="text-xl font-semibold">{r?.title ?? code}</h1>
          <p className="text-sm text-text-muted">{fund.name}</p>
        </div>
        {fund.permissions.export && (
          <Button variant="secondary" className="ml-auto" onClick={() => setExportOpen(true)}><Download aria-hidden className="size-4" />{t('reports.export')}</Button>
        )}
      </div>

      <div role="radiogroup" aria-label={t('reports.period')} className="flex flex-wrap gap-2">
        {presets.map((p) => (
          <label key={p} className={`flex min-h-11 cursor-pointer items-center rounded-full border px-3 text-sm ${preset === p ? 'border-primary bg-primary text-primary-fg' : 'border-border'}`}>
            <input type="radio" name="period" value={p} checked={preset === p} onChange={() => setPreset(p)} className="sr-only" />
            {t(`reports.presets.${p}`)}
          </label>
        ))}
      </div>
      {preset === 'custom' && (
        <div className="grid max-w-md grid-cols-2 gap-3">
          <Field label={t('audit.from')} type="date" value={customFrom} max={customTo || undefined} onChange={(e) => setCustomFrom(e.target.value)} />
          <Field label={t('audit.to')} type="date" value={customTo} min={customFrom || undefined} onChange={(e) => setCustomTo(e.target.value)} />
        </div>
      )}
      {(LIST_REPORTS.has(code) || ACCOUNT_FILTER.has(code)) && (
        <div className="grid max-w-xl grid-cols-2 gap-3">
          {LIST_REPORTS.has(code) && (
            <Select label={t('txn.category')} value={categoryId} onChange={setCategoryId} options={(categories.data ?? []).map((c) => ({ id: c.id, label: c.name }))} />
          )}
          <Select label={t('txn.account')} value={accountId} onChange={setAccountId} options={(accounts.data ?? []).map((a) => ({ id: a.id, label: a.name }))} />
        </div>
      )}

      <ErrorBanner message={report.error?.message} />
      {report.isPending && <p className="text-text-muted">{t('app.loading')}</p>}

      {r && (
        <>
          <p className="text-sm text-text-muted">
            {formatDate(r.from)} – {formatDate(r.to)}
            {r.ownOnly && <> · <Badge tone="warning">{t('ledger.ownOnly')}</Badge></>}
          </p>
          <ul className="grid grid-cols-2 gap-3 md:grid-cols-3" aria-label={t('reports.summary')}>
            {r.summary.map((f) => (
              <li key={f.key} className="rounded-lg border border-border bg-surface p-3">
                <p className="text-xs text-text-muted">{f.label}</p>
                <p className="amount text-lg font-semibold">{f.kind === 'MONEY' ? formatRupees(f.value) : f.value}</p>
              </li>
            ))}
          </ul>
          {r.rows.length === 0 ? <p className="py-8 text-center text-text-muted">{t('reports.empty')}</p> : <ReportDetail report={r} />}
          {r.truncated && <p role="note" className="text-sm text-text-muted">{t('reports.truncated')}</p>}
        </>
      )}

      {r && <ExportDialog open={exportOpen} onClose={() => setExportOpen(false)} code={code} fundId={fund.id} period={period} filters={filters} />}
    </div>
  )
}

function Select({ label, value, onChange, options }: { label: string; value: string; onChange: (v: string) => void; options: { id: string; label: string }[] }) {
  const { t } = useTranslation()
  return (
    <label className="grid gap-1 text-sm font-medium">
      {label}
      <select value={value} onChange={(e) => onChange(e.target.value)} className="min-h-11 rounded-md border border-border bg-surface px-2 font-normal">
        <option value="">{t('ledger.all')}</option>
        {options.map((o) => <option key={o.id} value={o.id}>{o.label}</option>)}
      </select>
    </label>
  )
}

function ReportDetail({ report }: { report: Report }) {
  const numeric = (c: Column) => c.kind === 'MONEY' || c.kind === 'NUMBER'
  const totals = report.totals
  return (
    <>
      {/* Desktop and tablets: a table */}
      <div className="hidden overflow-x-auto rounded-lg border border-border md:block">
        <table className="w-full text-sm">
          <thead className="bg-surface-muted">
            <tr>{report.columns.map((c) => <th key={c.key} scope="col" className={`px-3 py-2 font-semibold ${numeric(c) ? 'text-right' : 'text-left'}`}>{c.label}</th>)}</tr>
          </thead>
          <tbody>
            {report.rows.map((row, i) => (
              <tr key={i} className="border-t border-border">
                {report.columns.map((c) => <td key={c.key} className={`px-3 py-2 ${numeric(c) ? 'amount text-right' : ''}`}>{cellText(c, row[c.key])}</td>)}
              </tr>
            ))}
          </tbody>
          {totals && (
            <tfoot>
              <tr className="border-t-2 border-border bg-surface-muted font-semibold">
                {report.columns.map((c) => <td key={c.key} className={`px-3 py-2 ${numeric(c) ? 'amount text-right' : ''}`}>{cellText(c, totals[c.key])}</td>)}
              </tr>
            </tfoot>
          )}
        </table>
      </div>

      {/* Phones: one card per row */}
      <ul className="grid gap-2 md:hidden">
        {report.rows.map((row, i) => (
          <li key={i} className="grid gap-1 rounded-lg border border-border bg-surface p-3 text-sm">
            {report.columns.filter((c) => cellText(c, row[c.key]) !== '').map((c) => (
              <div key={c.key} className="flex justify-between gap-3">
                <span className="text-text-muted">{c.label}</span>
                <span className={numeric(c) ? 'amount font-medium' : 'text-right'}>{cellText(c, row[c.key])}</span>
              </div>
            ))}
          </li>
        ))}
        {totals && (
          <li className="grid gap-1 rounded-lg border-2 border-border bg-surface-muted p-3 text-sm font-semibold">
            {report.columns.filter((c) => cellText(c, totals[c.key]) !== '').map((c) => (
              <div key={c.key} className="flex justify-between gap-3"><span>{c.label}</span><span className="amount">{cellText(c, totals[c.key])}</span></div>
            ))}
          </li>
        )}
      </ul>
    </>
  )
}
