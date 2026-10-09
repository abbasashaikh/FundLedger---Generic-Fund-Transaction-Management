import { Link } from 'react-router'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Plus } from 'lucide-react'
import { api } from '../../lib/api/client'
import { unwrap } from '../../lib/api/errors'
import { formatDate } from '../../lib/format/date'
import { formatRupees } from '../../lib/format/money'
import { Badge, ErrorBanner } from '../../components/ui'

const TONE = { DRAFT: 'muted', ACTIVE: 'success', CLOSED: 'warning', ARCHIVED: 'muted' } as const

/** S20 — all funds with status and balance (Admin). */
export function FundsPage() {
  const { t } = useTranslation()
  const funds = useQuery({ queryKey: ['funds', 'manage'], queryFn: async () => unwrap(await api.GET('/api/v1/funds/manage')) })
  return (
    <div className="mx-auto grid max-w-3xl gap-4">
      <div className="flex items-center gap-3">
        <h1 className="text-2xl font-semibold">{t('screens.funds')}</h1>
        <Link to="/admin/funds/new" className="ml-auto inline-flex min-h-11 items-center gap-2 rounded-md bg-primary px-4 text-sm font-medium text-primary-fg">
          <Plus aria-hidden className="size-4" />{t('funds.create')}
        </Link>
      </div>
      <ErrorBanner message={funds.error?.message} />
      {funds.isPending && <p className="text-text-muted">{t('app.loading')}</p>}
      {funds.data?.length === 0 && <p className="py-8 text-center text-text-muted">{t('funds.none')}</p>}
      <ul className="grid gap-2" aria-label={t('screens.funds')}>
        {funds.data?.map((f) => (
          <li key={f.id}>
            <Link to={`/admin/funds/${f.id}`} className="flex min-h-16 flex-wrap items-center gap-x-3 gap-y-1 rounded-md border border-border bg-surface px-4 py-2 hover:bg-surface-muted">
              <span className="font-medium">{f.name}</span>
              <span className="text-sm text-text-muted">{f.code} · {f.fundTypeName}</span>
              <Badge tone={TONE[f.status]}>{t(`funds.status.${f.status}`)}</Badge>
              <span className="amount ml-auto font-semibold">{formatRupees(f.closingBalance)}</span>
              {(f.startDate || f.endDate) && <span className="w-full text-xs text-text-muted">{f.startDate ? formatDate(f.startDate) : '…'} – {f.endDate ? formatDate(f.endDate) : '…'}</span>}
            </Link>
          </li>
        ))}
      </ul>
    </div>
  )
}
