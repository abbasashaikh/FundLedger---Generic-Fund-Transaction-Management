import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { api, type Schemas } from '../lib/api/client'
import { unwrap } from '../lib/api/errors'
import { addDays, todayIso } from '../lib/format/date'
import { formatRupees } from '../lib/format/money'
import { Section } from './ui/money'

type Report = Schemas['ReportResult']

/**
 * Desktop dashboard charts (App Flow §4.1 item 7): money in/out for the last 30 days and the top five spending
 * categories. They read the same report endpoints as the Reports screen, so the numbers always agree with it.
 * Amounts are decimal strings; they are only scaled to bar heights here, never summed or shown from a float.
 */
export function DashboardCharts({ fundId }: { fundId: string }) {
  const { t } = useTranslation()
  const today = todayIso()
  const from = addDays(today, -29)
  const daily = useQuery<Report>({
    queryKey: ['dashboard', fundId, 'trend', from],
    queryFn: async () => unwrap(await api.GET('/api/v1/reports/{code}', { params: { path: { code: 'DAILY' }, query: { fundId, from, to: today } } })),
  })
  const categories = useQuery<Report>({
    queryKey: ['dashboard', fundId, 'top-categories', from],
    queryFn: async () => unwrap(await api.GET('/api/v1/reports/{code}', { params: { path: { code: 'CATEGORY' }, query: { fundId, from, to: today } } })),
  })

  // One slot per calendar day, so a quiet month doesn't turn a single entry into one huge bar.
  const byDate = new Map((daily.data?.rows ?? []).map((r) => [r.date, r]))
  const days = Array.from({ length: 30 }, (_, i) => {
    const date = addDays(from, i)
    return { date, moneyIn: byDate.get(date)?.moneyIn ?? '0', moneyOut: byDate.get(date)?.moneyOut ?? '0' }
  })
  const hasData = (daily.data?.rows.length ?? 0) > 0
  const top = (categories.data?.rows ?? []).filter((r) => r.direction === 'Money out').slice(0, 5)
  const max = Math.max(1, ...days.flatMap((d) => [Number(d.moneyIn), Number(d.moneyOut)]))
  const topMax = Math.max(1, ...top.map((r) => Number(r.amount)))

  return (
    <div className="hidden gap-4 lg:grid lg:grid-cols-2">
      <Section title={t('dashboard.trend')} id="trend-h">
        {daily.isSuccess && !hasData ? <p className="text-sm text-text-muted">{t('dashboard.noTrend')}</p> : (
          <div role="img" aria-label={t('dashboard.trend')} className="flex h-40 items-end gap-1">
            {days.map((d) => (
              <div key={d.date} className="flex h-full min-w-0 flex-1 flex-col justify-end gap-0.5" title={`${d.date}: ↙ ${formatRupees(d.moneyIn ?? '0')}  ↗ ${formatRupees(d.moneyOut ?? '0')}`}>
                <div className="rounded-t-sm bg-in" style={{ height: `${(Number(d.moneyIn) / max) * 48}%` }} />
                <div className="rounded-t-sm bg-out" style={{ height: `${(Number(d.moneyOut) / max) * 48}%` }} />
              </div>
            ))}
          </div>
        )}
        <p className="flex gap-4 text-xs text-text-muted"><span><span aria-hidden className="text-in">■</span> {t('ledger.in')}</span><span><span aria-hidden className="text-out">■</span> {t('ledger.out')}</span></p>
      </Section>

      <Section title={t('dashboard.topCategories')} id="top-h">
        {top.length === 0 ? <p className="text-sm text-text-muted">{t('dashboard.noTrend')}</p> : (
          <ul className="grid gap-2">
            {top.map((r) => (
              <li key={r.category} className="grid gap-1 text-sm">
                <span className="flex justify-between"><span>{r.category}</span><span className="amount font-medium">{formatRupees(r.amount ?? '0', { decimals: 'auto' })}</span></span>
                <span className="h-2 rounded-full bg-surface-muted"><span className="block h-2 rounded-full bg-out" style={{ width: `${(Number(r.amount) / topMax) * 100}%` }} /></span>
              </li>
            ))}
          </ul>
        )}
      </Section>
    </div>
  )
}
