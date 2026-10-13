import { useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router'
import { useInfiniteQuery, useQuery, type InfiniteData } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Search, SlidersHorizontal } from 'lucide-react'
import { api, type Schemas } from '../../lib/api/client'
import { unwrap } from '../../lib/api/errors'
import { useAccounts, useCategories, type Txn } from '../../lib/api/hooks'
import { useMe, useSelectedFund } from '../../lib/auth/queries'
import { addDays, dayHeading, startOfMonth, startOfWeek, todayIso } from '../../lib/format/date'
import { formatRupees } from '../../lib/format/money'
import { Badge, Button, ErrorBanner } from '../../components/ui'
import { TxnRow } from '../../components/TxnRow'
import { useSession } from '../../lib/auth/session'
import { entryTitle, useOutbox } from '../../lib/offline/outbox'
import { isNetworkError, loadLedgerCache, saveLedgerCache } from '../../lib/offline/refdata'
import { formatDateTime } from '../../lib/format/date'

type Preset = 'all' | 'today' | 'yesterday' | 'week' | 'month'

function range(preset: Preset): { from?: string; to?: string } {
  const today = todayIso()
  switch (preset) {
    case 'today': return { from: today, to: today }
    case 'yesterday': return { from: addDays(today, -1), to: addDays(today, -1) }
    case 'week': return { from: startOfWeek(today), to: today }
    case 'month': return { from: startOfMonth(today), to: today }
    default: return {}
  }
}

/** S06 — the ledger of the selected fund: search, filters, sort, totals, paging (App Flow §4.5). */
export function LedgerPage() {
  const { t } = useTranslation()
  const me = useMe()
  const fund = useSelectedFund(me.data)

  const [search, setSearch] = useState('')
  const [q, setQ] = useState('')
  const [preset, setPreset] = useState<Preset>('all')
  const [type, setType] = useState('')
  const [status, setStatus] = useState('')
  const [categoryId, setCategoryId] = useState('')
  const [accountId, setAccountId] = useState('')
  const [sort, setSort] = useState('NEWEST')
  const [filtersOpen, setFiltersOpen] = useState(false)

  // Debounce typing so the server is asked once the user pauses (App Flow §4.5).
  useEffect(() => {
    const timer = setTimeout(() => setQ(search.trim()), 300)
    return () => clearTimeout(timer)
  }, [search])

  const categories = useCategories(undefined, fund?.id)
  const accounts = useAccounts()
  const { from, to } = range(preset)

  const query = useInfiniteQuery<Schemas['TransactionPage'], Error, InfiniteData<Schemas['TransactionPage'], string | undefined>, unknown[], string | undefined>({
    queryKey: ['transactions', fund?.id, { q, from, to, type, status, categoryId, accountId, sort }],
    enabled: !!fund,
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    queryFn: async ({ pageParam }) =>
      unwrap(await api.GET('/api/v1/transactions', {
        params: { query: {
          fundId: fund!.id, q: q || undefined, from, to, type: type || undefined, status: status || undefined,
          categoryId: categoryId || undefined, accountId: accountId || undefined, sort, cursor: pageParam, limit: 50,
        } },
      })),
  })

  const userId = useSession((s) => s.user?.id)
  const pendingHere = useOutbox(userId).filter((i) => i.fundId === fund?.id)
  const unfiltered = !q && preset === 'all' && !type && !status && !categoryId && !accountId && sort === 'NEWEST'

  // Offline (TRD §8.2 `ledgerCache`): the last entries seen on this device, read-only and clearly labelled.
  const offlineFailure = query.isError && isNetworkError(query.error)
  const cache = useQuery({ queryKey: ['ledger-cache', userId, fund?.id], enabled: offlineFailure && !!userId && !!fund, staleTime: 0, queryFn: async () => (await loadLedgerCache(userId!, fund!.id)) ?? null })

  const pages = useMemo(() => query.data?.pages ?? [], [query.data])
  const fetched = useMemo(() => pages.flatMap((p) => p.items), [pages])
  const cachedItems = cache.data?.items
  const items = useMemo(() => (offlineFailure ? (cachedItems ?? []) : fetched), [offlineFailure, cachedItems, fetched])

  useEffect(() => {
    if (userId && fund && unfiltered && query.isSuccess && fetched.length > 0) void saveLedgerCache(userId, fund.id, fetched)
  }, [userId, fund, unfiltered, query.isSuccess, fetched.length]) // eslint-disable-line react-hooks/exhaustive-deps
  const totals = pages[0]?.totals
  const groups = useMemo(() => {
    const map = new Map<string, Txn[]>()
    for (const tx of items) map.set(tx.txnDate, [...(map.get(tx.txnDate) ?? []), tx])
    return [...map.entries()]
  }, [items])

  if (me.isPending) return <p className="text-text-muted">{t('app.loading')}</p>
  if (!fund) return <p className="py-16 text-center text-text-muted">{t('txn.noFund')}</p>

  const activeFilters = [type, status, categoryId, accountId].filter(Boolean).length + (preset !== 'all' ? 1 : 0)
  const reset = () => { setPreset('all'); setType(''); setStatus(''); setCategoryId(''); setAccountId('') }

  return (
    <div className="grid gap-4">
      <div className="flex flex-wrap items-center gap-2">
        <h1 className="text-2xl font-semibold">{t('screens.ledger')}</h1>
        {pages[0]?.ownOnly && <Badge tone="warning">{t('ledger.ownOnly')}</Badge>}
      </div>

      <div className="flex gap-2">
        <label className="flex min-h-11 flex-1 items-center gap-2 rounded-md border border-border bg-surface px-3">
          <Search aria-hidden className="size-4 text-text-muted" />
          <span className="sr-only">{t('ledger.search')}</span>
          <input type="search" value={search} onChange={(e) => setSearch(e.target.value)} placeholder={t('ledger.search')} className="min-h-11 w-full bg-transparent outline-none" />
        </label>
        <Button variant="secondary" onClick={() => setFiltersOpen((o) => !o)} aria-expanded={filtersOpen}>
          <SlidersHorizontal aria-hidden className="size-4" />{t('ledger.filters')}{activeFilters > 0 && ` (${activeFilters})`}
        </Button>
      </div>

      {filtersOpen && (
        <div className="grid gap-3 rounded-lg border border-border bg-surface p-3 sm:grid-cols-2">
          <Select label={t('ledger.period')} value={preset} onChange={(v) => setPreset(v as Preset)}
            options={[['all', t('ledger.all')], ['today', t('txn.today')], ['yesterday', t('ledger.yesterday')], ['week', t('ledger.thisWeek')], ['month', t('ledger.thisMonth')]]} />
          <Select label={t('ledger.type')} value={type} onChange={setType}
            options={[['', t('ledger.all')], ['DEPOSIT', t('txn.type.DEPOSIT')], ['EXPENSE', t('txn.type.EXPENSE')], ['TRANSFER', t('txn.type.TRANSFER')], ['ADJUSTMENT', t('txn.type.ADJUSTMENT')]]} />
          <Select label={t('txn.category')} value={categoryId} onChange={setCategoryId}
            options={[['', t('ledger.all')], ...(categories.data ?? []).map((c): [string, string] => [c.id, c.name])]} />
          <Select label={t('txn.account')} value={accountId} onChange={setAccountId}
            options={[['', t('ledger.all')], ...(accounts.data ?? []).map((a): [string, string] => [a.id, a.name])]} />
          <Select label={t('ledger.status')} value={status} onChange={setStatus}
            options={[['', t('ledger.all')], ['ACTIVE', t('ledger.statusActive')], ['CANCELLED', t('txn.cancelled')]]} />
          <Select label={t('ledger.sort')} value={sort} onChange={setSort}
            options={[['NEWEST', t('ledger.newest')], ['OLDEST', t('ledger.oldest')], ['HIGHEST', t('ledger.highest')], ['LOWEST', t('ledger.lowest')]]} />
          {activeFilters > 0 && <Button variant="ghost" onClick={reset} className="sm:col-span-2">{t('ledger.clear')}</Button>}
        </div>
      )}

      {totals && (
        <p className="amount flex flex-wrap gap-x-4 rounded-md bg-surface-muted px-3 py-2 text-sm" aria-label={t('ledger.totals')}>
          <span className="text-in">{t('ledger.in')} {formatRupees(totals.moneyIn, { decimals: 'auto' })}</span>
          <span className="text-out">{t('ledger.out')} {formatRupees(totals.moneyOut, { decimals: 'auto' })}</span>
          <span className="font-semibold">{t('ledger.net')} {formatRupees(totals.net, { decimals: 'auto' })}</span>
        </p>
      )}

      {offlineFailure && cache.data && <p role="status" className="rounded-md border border-warning bg-surface px-3 py-2 text-sm">{t('ledger.savedData', { when: formatDateTime(cache.data.savedAt) })}</p>}
      {pendingHere.length > 0 && (
        <section aria-label={t('ledger.pendingHeading')} className="grid gap-2">
          <h2 className="text-sm font-semibold text-text-muted">{t('ledger.pendingHeading')}</h2>
          {pendingHere.map((i) => (
            <Link key={i.clientTxnId} to="/sync" className="flex items-center gap-2 rounded-lg border border-dashed border-border bg-surface p-3 text-sm">
              <span className="min-w-0 flex-1 truncate">{entryTitle(i)} · {i.command.purpose}</span>
              <span className="amount font-semibold">{formatRupees(i.command.amount, { decimals: 'auto' })}</span>
              <Badge tone={i.state === 'NEEDS_ATTENTION' ? 'danger' : 'warning'}>{t(`sync.state.${i.state}`)}</Badge>
            </Link>
          ))}
        </section>
      )}
      <ErrorBanner message={offlineFailure && cache.data ? null : query.error?.message} />
      {query.isPending && <p className="text-text-muted">{t('app.loading')}</p>}
      {query.isSuccess && items.length === 0 && (
        <div className="py-12 text-center">
          <p className="font-medium">{activeFilters > 0 || q ? t('ledger.noMatches') : t('ledger.empty')}</p>
          {!(activeFilters > 0 || q) && <p className="text-text-muted">{t('ledger.emptyHint')}</p>}
        </div>
      )}

      {groups.map(([day, rows]) => (
        <section key={day} aria-label={dayHeading(day)} className="grid gap-2">
          <h2 className="sticky top-14 z-10 bg-bg py-1 text-sm font-semibold text-text-muted">{dayHeading(day)}</h2>
          {rows.map((tx) => <TxnRow key={tx.id} txn={tx} />)}
        </section>
      ))}

      {query.hasNextPage && (
        <Button variant="secondary" loading={query.isFetchingNextPage} onClick={() => void query.fetchNextPage()}>{t('ledger.loadMore')}</Button>
      )}
    </div>
  )
}

function Select({ label, value, onChange, options }: { label: string; value: string; onChange: (v: string) => void; options: [string, string][] }) {
  const id = `f-${label}`
  return (
    <div className="grid gap-1">
      <label htmlFor={id} className="text-sm font-medium">{label}</label>
      <select id={id} value={value} onChange={(e) => onChange(e.target.value)} className="min-h-11 rounded-md border border-border bg-surface px-3">
        {options.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
      </select>
    </div>
  )
}
