import { useEffect, useState } from 'react'
import { Link } from 'react-router'
import { useInfiniteQuery, type InfiniteData } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Download, Search } from 'lucide-react'
import { api, type Schemas } from '../../lib/api/client'
import { ApiError, unwrap } from '../../lib/api/errors'
import { formatDateTime } from '../../lib/format/date'
import { Button, ErrorBanner, Field } from '../../components/ui'
import { toast } from '../../lib/toast'

type Page = Schemas['AuditPage']
type Item = Schemas['AuditItem']

const GROUPS = ['', 'TRANSACTIONS', 'FUNDS', 'USERS', 'MASTER', 'AUTH', 'SYSTEM'] as const

/** S19 — the audit log (App Flow §5.4, PRD §14): who did what, filterable, exportable to CSV. Admin only. */
export function AuditLogPage() {
  const { t } = useTranslation()
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [group, setGroup] = useState('')
  const [search, setSearch] = useState('')
  const [q, setQ] = useState('')
  const [exporting, setExporting] = useState(false)

  useEffect(() => {
    const timer = setTimeout(() => setQ(search.trim()), 300)
    return () => clearTimeout(timer)
  }, [search])

  const filters = { from: from || undefined, to: to || undefined, group: group || undefined, q: q || undefined }
  const query = useInfiniteQuery<Page, ApiError, InfiniteData<Page, string | undefined>, unknown[], string | undefined>({
    queryKey: ['audit-logs', filters],
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    queryFn: async ({ pageParam }) =>
      unwrap(await api.GET('/api/v1/audit-logs', { params: { query: { ...filters, cursor: pageParam, limit: 50 } } })),
  })
  const items = query.data?.pages.flatMap((p) => p.items) ?? []

  async function exportCsv() {
    setExporting(true)
    try {
      const result = await api.GET('/api/v1/audit-logs/export', { params: { query: filters }, parseAs: 'blob' })
      const blob = unwrap(result) as unknown as Blob
      const name = /filename="?([^";]+)"?/.exec(result.response.headers.get('Content-Disposition') ?? '')?.[1] ?? 'audit-log.csv'
      const url = URL.createObjectURL(blob)
      const a = document.createElement('a')
      a.href = url
      a.download = name
      a.click()
      URL.revokeObjectURL(url)
    } catch (err) {
      toast.error(err instanceof ApiError ? err.message : t('errors.generic'))
    } finally {
      setExporting(false)
    }
  }

  return (
    <div className="mx-auto grid max-w-4xl gap-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h1 className="text-2xl font-semibold">{t('screens.audit')}</h1>
        <Button variant="secondary" loading={exporting} onClick={() => void exportCsv()}><Download aria-hidden className="size-4" />{t('audit.export')}</Button>
      </div>

      <div className="grid gap-3 rounded-lg border border-border bg-surface p-3 sm:grid-cols-4">
        <Field label={t('audit.from')} type="date" value={from} max={to || undefined} onChange={(e) => setFrom(e.target.value)} />
        <Field label={t('audit.to')} type="date" value={to} min={from || undefined} onChange={(e) => setTo(e.target.value)} />
        <label className="grid gap-1 text-sm font-medium">
          {t('audit.group')}
          <select value={group} onChange={(e) => setGroup(e.target.value)} className="min-h-11 rounded-md border border-border bg-surface px-2 font-normal">
            {GROUPS.map((g) => <option key={g} value={g}>{t(`audit.groups.${g || 'ALL'}`)}</option>)}
          </select>
        </label>
        <div className="relative">
          <Field label={t('audit.search')} type="search" value={search} onChange={(e) => setSearch(e.target.value)} placeholder={t('audit.searchHint')} />
          <Search aria-hidden className="pointer-events-none absolute right-3 bottom-3 size-4 text-text-muted" />
        </div>
      </div>
      <p className="text-xs text-text-muted">{t('audit.exportNote')}</p>

      {query.isError && <ErrorBanner message={query.error.message} />}
      {query.isPending && <p className="text-text-muted">{t('app.loading')}</p>}
      {query.isSuccess && items.length === 0 && <p className="py-8 text-center text-text-muted">{t('audit.empty')}</p>}

      <ul className="grid gap-2">
        {items.map((item) => <AuditRow key={item.id} item={item} />)}
      </ul>

      {query.hasNextPage && (
        <Button variant="secondary" loading={query.isFetchingNextPage} onClick={() => void query.fetchNextPage()}>{t('audit.loadMore')}</Button>
      )}
    </div>
  )
}

function AuditRow({ item }: { item: Item }) {
  const { t } = useTranslation()
  const hasValues = item.oldValue != null || item.newValue != null
  const isTxn = item.entityType === 'Transaction' && item.entityId
  return (
    <li className="rounded-lg border border-border bg-surface p-3 text-sm">
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <p>
          <span className="font-mono text-xs font-semibold">{item.action}</span>
          {' · '}{item.user?.name ?? t('audit.system')}
          {item.fundName && <span className="text-text-muted"> · {item.fundName}</span>}
        </p>
        <time className="text-xs text-text-muted" dateTime={item.createdAt}>{formatDateTime(item.createdAt)}</time>
      </div>
      <p className="text-xs text-text-muted">
        {item.entityType}
        {item.entityId && <> · {isTxn ? <Link className="text-primary underline" to={`/txn/${item.entityId}`}>{item.entityId}</Link> : item.entityId}</>}
      </p>
      {item.reason && <p className="mt-1">{t('txn.historyReason', { reason: item.reason })}</p>}
      {hasValues && (
        <details className="mt-1">
          <summary className="cursor-pointer text-xs text-primary">{t('audit.showChanges')}</summary>
          <div className="mt-2 grid gap-2 sm:grid-cols-2">
            <Values title={t('audit.before')} value={item.oldValue} />
            <Values title={t('audit.after')} value={item.newValue} />
          </div>
        </details>
      )}
    </li>
  )
}

function Values({ title, value }: { title: string; value: unknown }) {
  return (
    <div>
      <p className="text-xs font-medium text-text-muted">{title}</p>
      <pre className="mt-1 max-h-60 overflow-auto rounded-md bg-surface-muted p-2 text-xs whitespace-pre-wrap break-all">
        {value == null ? '—' : JSON.stringify(value, null, 2)}
      </pre>
    </div>
  )
}
