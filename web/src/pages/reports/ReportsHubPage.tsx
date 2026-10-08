import { Link } from 'react-router'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { BarChart3, CalendarDays, Download, FileMinus, FileSpreadsheet, FileText, Landmark, ListChecks, PieChart, Users, type LucideIcon } from 'lucide-react'
import { api, type Schemas } from '../../lib/api/client'
import { unwrap } from '../../lib/api/errors'
import { useMe, useSelectedFund } from '../../lib/auth/queries'
import { ErrorBanner } from '../../components/ui'

const ICONS: Record<string, LucideIcon> = {
  FUND_SUMMARY: BarChart3, DAILY: CalendarDays, DATE_RANGE: FileSpreadsheet, MONEY_IN: FileText, MONEY_OUT: FileMinus,
  TRANSFER: Landmark, USER_ACTIVITY: Users, CATEGORY: PieChart, ACCOUNT_BALANCE: ListChecks, CANCELLED: FileMinus,
}

/** S14 — the report cards for the selected fund (App Flow §4.9). */
export function ReportsHubPage() {
  const { t } = useTranslation()
  const me = useMe()
  const fund = useSelectedFund(me.data)
  const catalog = useQuery<Schemas['ReportCatalogItem'][]>({
    queryKey: ['reports', 'catalog'],
    staleTime: Infinity,
    queryFn: async () => unwrap(await api.GET('/api/v1/reports')),
  })

  if (me.isPending) return <p className="text-text-muted">{t('app.loading')}</p>
  if (!fund) return <p className="py-16 text-center text-text-muted">{t('txn.noFund')}</p>
  if (!fund.permissions.viewReports) return <p className="py-16 text-center">{t('reports.noAccess')}</p>

  return (
    <div className="mx-auto grid max-w-4xl gap-4">
      <div className="flex flex-wrap items-center gap-2">
        <div>
          <h1 className="text-2xl font-semibold">{t('screens.reports')}</h1>
          <p className="text-sm text-text-muted">{fund.name}</p>
        </div>
        {fund.permissions.export && (
          <Link to="/reports/exports" className="ml-auto inline-flex min-h-11 items-center gap-2 rounded-md border border-border px-3 text-sm font-medium hover:bg-surface-muted">
            <Download aria-hidden className="size-4" />{t('reports.myDownloads')}
          </Link>
        )}
      </div>
      {!fund.permissions.viewAllTransactions && <p role="note" className="rounded-md border border-warning bg-surface px-3 py-2 text-sm">{t('ledger.ownOnly')}</p>}
      <ErrorBanner message={catalog.error?.message} />
      <ul className="grid gap-3 sm:grid-cols-2">
        {(catalog.data ?? []).map((r) => {
          const Icon = ICONS[r.code] ?? BarChart3
          return (
            <li key={r.code}>
              <Link to={`/reports/${r.code}`} className="flex min-h-20 items-start gap-3 rounded-lg border border-border bg-surface p-4 hover:bg-surface-muted">
                <Icon aria-hidden className="mt-0.5 size-6 shrink-0 text-primary" />
                <span>
                  <span className="block font-semibold">{r.title}</span>
                  <span className="block text-sm text-text-muted">{r.description}</span>
                </span>
              </Link>
            </li>
          )
        })}
      </ul>
    </div>
  )
}
