import { useState } from 'react'
import { NavLink, Outlet } from 'react-router'
import { useTranslation } from 'react-i18next'
import {
  ArrowDownLeft, ArrowRightLeft, ArrowUpRight, BarChart3, BookOpen, Building2, ChevronDown, ClipboardList,
  Cog, FolderKanban, Home, List, MoreHorizontal, Plus, Tags, Users, Wallet, CheckCircle2,
} from 'lucide-react'
import { QuickActionSheet } from './QuickActionSheet'
import { UpdatePrompt } from '../../pwa/UpdatePrompt'
import { OfflineBanner } from '../../pwa/OfflineBanner'

// Layout per docs/02-App-Flow.md §2: bottom nav + centre (＋) on mobile,
// left sidebar on desktop (≥1024px). Fund switcher and sync indicator are
// visual placeholders until Phase 2 / Phase 5 wire them to data.
export function AppShell() {
  const { t } = useTranslation()
  const [sheetOpen, setSheetOpen] = useState(false)

  const main = [
    { to: '/', label: t('nav.home'), icon: Home, end: true },
    { to: '/ledger', label: t('nav.ledger'), icon: BookOpen },
    { to: '/reports', label: t('nav.reports'), icon: BarChart3 },
  ]
  const admin = [
    { to: '/admin/users', label: t('nav.users'), icon: Users },
    { to: '/admin/funds', label: t('nav.funds'), icon: FolderKanban },
    { to: '/admin/accounts', label: t('nav.accounts'), icon: Wallet },
    { to: '/admin/categories', label: t('nav.categories'), icon: Tags },
    { to: '/admin/lookups', label: t('nav.lookups'), icon: List },
    { to: '/admin/audit', label: t('nav.audit'), icon: ClipboardList },
    { to: '/admin/settings', label: t('nav.settings'), icon: Cog },
  ]

  return (
    <div className="min-h-dvh bg-bg text-text lg:grid lg:grid-cols-[240px_1fr]">
      <a href="#main" className="sr-only focus:not-sr-only focus:absolute focus:left-2 focus:top-2 focus:z-50 focus:rounded-md focus:bg-surface focus:p-2">
        {t('nav.skipToContent')}
      </a>

      {/* Desktop sidebar */}
      <aside className="hidden border-r border-border bg-surface lg:flex lg:flex-col" aria-label={t('nav.primary')}>
        <div className="flex h-14 items-center gap-2 px-4 text-lg font-semibold">
          <Building2 aria-hidden className="size-5 text-primary" />
          <span>Fund<span className="font-bold">Ledger</span></span>
        </div>
        <nav className="flex flex-col gap-1 px-2">
          {main.map((item) => <SideLink key={item.to} {...item} />)}
          <p className="mt-4 px-3 text-xs font-medium uppercase tracking-wide text-text-muted">{t('nav.administration')}</p>
          {admin.map((item) => <SideLink key={item.to} {...item} />)}
        </nav>
      </aside>

      <div className="flex min-h-dvh flex-col">
        {/* Top bar: fund switcher, quick actions (desktop), sync status */}
        <header className="sticky top-0 z-20 flex h-14 items-center gap-2 border-b border-border bg-surface px-3">
          <button type="button" className="flex min-h-11 items-center gap-1 rounded-md px-2 font-semibold hover:bg-surface-muted" aria-label={t('fund.switch')}>
            {t('fund.noneSelected')} <ChevronDown aria-hidden className="size-4" />
          </button>
          <div className="ml-auto hidden gap-2 lg:flex">
            <QuickLink to="/new/in" icon={ArrowDownLeft} label={t('actions.moneyIn')} tone="text-in" />
            <QuickLink to="/new/out" icon={ArrowUpRight} label={t('actions.moneyOut')} tone="text-out" />
            <QuickLink to="/new/transfer" icon={ArrowRightLeft} label={t('actions.transfer')} tone="text-transfer" />
          </div>
          <span className="ml-auto flex min-h-11 items-center gap-1 text-sm text-text-muted lg:ml-2" title={t('sync.allSynced')}>
            <CheckCircle2 aria-hidden className="size-5 text-success" />
            <span className="sr-only">{t('sync.allSynced')}</span>
          </span>
        </header>

        <OfflineBanner />
        <UpdatePrompt />

        <main id="main" className="mx-auto w-full max-w-7xl flex-1 p-4 pb-24 lg:pb-6">
          <Outlet />
        </main>

        {/* Mobile bottom navigation */}
        <nav aria-label={t('nav.primary')} className="fixed inset-x-0 bottom-0 z-20 grid grid-cols-5 border-t border-border bg-surface pb-[env(safe-area-inset-bottom)] lg:hidden">
          <TabLink {...main[0]!} />
          <TabLink {...main[1]!} />
          <div className="flex items-center justify-center">
            <button
              type="button"
              onClick={() => setSheetOpen(true)}
              aria-haspopup="dialog"
              aria-label={t('nav.add')}
              className="-mt-6 flex size-14 items-center justify-center rounded-full bg-primary text-primary-fg shadow-lg"
            >
              <Plus aria-hidden className="size-7" />
            </button>
          </div>
          <TabLink {...main[2]!} />
          <TabLink to="/more" label={t('nav.more')} icon={MoreHorizontal} />
        </nav>
      </div>

      <QuickActionSheet open={sheetOpen} onClose={() => setSheetOpen(false)} />
    </div>
  )
}

type LinkProps = { to: string; label: string; icon: typeof Home; end?: boolean }

function SideLink({ to, label, icon: Icon, end }: LinkProps) {
  return (
    <NavLink
      to={to}
      end={end}
      className={({ isActive }) =>
        `flex min-h-11 items-center gap-3 rounded-md px-3 text-sm ${isActive ? 'bg-surface-muted font-semibold text-primary' : 'text-text hover:bg-surface-muted'}`
      }
    >
      <Icon aria-hidden className="size-5" />
      {label}
    </NavLink>
  )
}

function TabLink({ to, label, icon: Icon, end }: LinkProps) {
  return (
    <NavLink
      to={to}
      end={end}
      className={({ isActive }) =>
        `flex min-h-14 flex-col items-center justify-center gap-0.5 text-xs ${isActive ? 'font-semibold text-primary' : 'text-text-muted'}`
      }
    >
      <Icon aria-hidden className="size-5" />
      {label}
    </NavLink>
  )
}

function QuickLink({ to, icon: Icon, label, tone }: { to: string; icon: typeof Home; label: string; tone: string }) {
  return (
    <NavLink to={to} className="flex min-h-11 items-center gap-1.5 rounded-md border border-border px-3 text-sm font-medium hover:bg-surface-muted">
      <Icon aria-hidden className={`size-4 ${tone}`} />
      {label}
    </NavLink>
  )
}
