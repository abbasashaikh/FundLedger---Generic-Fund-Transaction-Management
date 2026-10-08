import { Link } from 'react-router'
import { useTranslation } from 'react-i18next'
import { ArrowDownLeft, ArrowRightLeft, ArrowUpRight, Landmark, Wallet } from 'lucide-react'
import { useDashboard } from '../lib/api/hooks'
import { useMe, useSelectedFund } from '../lib/auth/queries'
import { formatRupees } from '../lib/format/money'
import { Badge, ErrorBanner } from '../components/ui'
import { TxnRow } from '../components/TxnRow'
import { Section } from '../components/ui/money'

/** S04 — balance, today, quick actions, account balances, recent entries (App Flow §4.1). */
export function DashboardPage() {
  const { t } = useTranslation()
  const me = useMe()
  const fund = useSelectedFund(me.data)
  const dash = useDashboard(fund?.id)

  if (me.isPending) return <p className="text-text-muted">{t('app.loading')}</p>

  if (!fund) {
    // App Flow N-3: Members with no fund are told what to do; Admins are sent to create one.
    const admin = me.data?.role === 'ADMIN'
    return (
      <div className="mx-auto max-w-md py-16 text-center">
        <h1 className="text-2xl font-semibold">{t('screens.dashboard')}</h1>
        <p className="mt-2 text-text-muted">{admin ? t('dashboard.noFundsAdmin') : t('dashboard.noFundsMember')}</p>
        {admin && <Link to="/admin/funds/new" className="mt-4 inline-flex min-h-11 items-center rounded-md bg-primary px-4 font-medium text-primary-fg">{t('funds.create')}</Link>}
      </div>
    )
  }

  const p = fund.permissions
  const actions = [
    { show: p.moneyIn, to: '/new/in', label: t('actions.moneyIn'), icon: ArrowDownLeft, tone: 'text-in' },
    { show: p.moneyOut, to: '/new/out', label: t('actions.moneyOut'), icon: ArrowUpRight, tone: 'text-out' },
    { show: p.transfer, to: '/new/transfer', label: t('actions.transfer'), icon: ArrowRightLeft, tone: 'text-transfer' },
  ].filter((a) => a.show && fund.status === 'ACTIVE')

  const d = dash.data
  return (
    <div className="mx-auto grid max-w-3xl gap-4">
      <h1 className="sr-only">{t('screens.dashboard')}</h1>
      {fund.status === 'CLOSED' && <p role="status" className="rounded-md border border-border bg-surface-muted px-3 py-2 text-sm">{t('txn.closedFund', { fund: fund.name })}</p>}
      <ErrorBanner message={dash.error?.message} />

      <section aria-label={t('dashboard.balance')} className="grid gap-2 rounded-lg border border-border bg-surface p-5">
        <p className="text-sm text-text-muted">{t('dashboard.balance')}{d?.ownOnly && <> · <Badge tone="warning">{t('ledger.ownOnly')}</Badge></>}</p>
        <p className="amount text-4xl font-bold">{d ? formatRupees(d.balance) : '…'}</p>
        {d && (
          <p className="amount flex flex-wrap gap-x-4 text-sm">
            <span className="text-in">↙ {t('ledger.in')} {formatRupees(d.moneyIn, { decimals: 'auto' })}</span>
            <span className="text-out">↗ {t('ledger.out')} {formatRupees(d.moneyOut, { decimals: 'auto' })}</span>
          </p>
        )}
      </section>

      {d && (
        <p className="amount flex flex-wrap gap-x-6 text-sm">
          <span>{t('dashboard.today')}</span>
          <span className="text-in">+ {formatRupees(d.todayIn, { decimals: 'auto' })}</span>
          <span className="text-out">− {formatRupees(d.todayOut, { decimals: 'auto' })}</span>
        </p>
      )}

      {actions.length > 0 && (
        <div className="grid grid-cols-3 gap-2">
          {actions.map(({ to, label, icon: Icon, tone }) => (
            <Link key={to} to={to} className="flex min-h-14 flex-col items-center justify-center gap-1 rounded-md border border-border bg-surface text-sm font-medium hover:bg-surface-muted">
              <Icon aria-hidden className={`size-5 ${tone}`} />{label}
            </Link>
          ))}
        </div>
      )}

      {d && d.accounts.length > 0 && (
        <Section title={t('dashboard.accounts')} id="accounts-h">
          <ul className="grid gap-1">
            {d.accounts.map((a) => (
              <li key={a.accountId} className="flex min-h-11 items-center gap-2">
                {a.kind === 'CASH' ? <Wallet aria-hidden className="size-4 text-text-muted" /> : <Landmark aria-hidden className="size-4 text-text-muted" />}
                <span className="flex-1">{a.name}</span>
                <span className="amount font-semibold">{formatRupees(a.closing)}</span>
              </li>
            ))}
          </ul>
        </Section>
      )}

      {d && (
        <section aria-labelledby="recent-h" className="grid gap-2">
          <div className="flex items-center">
            <h2 id="recent-h" className="font-semibold">{t('dashboard.recent')}</h2>
            <Link to="/ledger" className="ml-auto text-sm font-medium text-primary">{t('dashboard.viewAll')}</Link>
          </div>
          {d.recent.length === 0 ? <p className="py-6 text-center text-text-muted">{t('ledger.emptyHint')}</p> : d.recent.map((tx) => <TxnRow key={tx.id} txn={tx} />)}
        </section>
      )}
    </div>
  )
}
