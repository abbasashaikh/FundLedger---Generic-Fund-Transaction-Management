import { useState } from 'react'
import { Link } from 'react-router'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Search, UserPlus } from 'lucide-react'
import { api } from '../../lib/api/client'
import { unwrap } from '../../lib/api/errors'
import { Badge, ErrorBanner } from '../../components/ui'
import { formatDateTime } from '../../lib/format/date'

/** S18 — Users list with active-user counter (App Flow §5.1). */
export function UsersPage() {
  const { t } = useTranslation()
  const [q, setQ] = useState('')
  const [status, setStatus] = useState<'ACTIVE' | 'INACTIVE' | ''>('')

  const users = useQuery({
    queryKey: ['users', q, status],
    queryFn: async () =>
      unwrap(await api.GET('/api/v1/users', { params: { query: { q: q || undefined, status: status || undefined } } })),
  })

  const data = users.data
  const counterTone = !data ? 'muted' : data.activeCount >= data.maxActiveUsers ? 'danger' : data.activeCount >= data.maxActiveUsers - 5 ? 'warning' : 'muted'
  const full = data ? data.activeCount >= data.maxActiveUsers : false

  return (
    <div className="grid gap-4">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="text-2xl font-semibold">{t('screens.users')}</h1>
        {data && <Badge tone={counterTone}>{t('users.activeCounter', { active: data.activeCount, max: data.maxActiveUsers })}</Badge>}
        {full ? (
          <span className="ml-auto text-sm text-text-muted" title={t('users.limitReached')}>{t('users.limitReached')}</span>
        ) : (
          <Link to="/admin/users/new" className="ml-auto inline-flex min-h-11 items-center gap-2 rounded-md bg-primary px-4 text-sm font-medium text-primary-fg">
            <UserPlus aria-hidden className="size-4" />{t('users.add')}
          </Link>
        )}
      </div>

      <div className="flex flex-wrap gap-2">
        <label className="flex min-h-11 flex-1 items-center gap-2 rounded-md border border-border bg-surface px-3">
          <Search aria-hidden className="size-4 text-text-muted" />
          <span className="sr-only">{t('users.search')}</span>
          <input value={q} onChange={(e) => setQ(e.target.value)} placeholder={t('users.search')} className="min-h-11 w-full bg-transparent outline-none" />
        </label>
        <label className="sr-only" htmlFor="status-filter">{t('users.status')}</label>
        <select id="status-filter" value={status} onChange={(e) => setStatus(e.target.value as typeof status)}
          className="min-h-11 rounded-md border border-border bg-surface px-3">
          <option value="">{t('users.allStatuses')}</option>
          <option value="ACTIVE">{t('users.statusActive')}</option>
          <option value="INACTIVE">{t('users.statusInactive')}</option>
        </select>
      </div>

      <ErrorBanner message={users.error?.message} />
      {users.isPending && <p className="text-text-muted">{t('app.loading')}</p>}

      {data && (
        <ul className="grid gap-2" aria-label={t('screens.users')}>
          {data.items.length === 0 && <li className="py-8 text-center text-text-muted">{t('users.none')}</li>}
          {data.items.map((u) => (
            <li key={u.id}>
              <Link to={`/admin/users/${u.id}`} className="flex min-h-16 flex-wrap items-center gap-x-3 gap-y-1 rounded-md border border-border bg-surface px-4 py-2 hover:bg-surface-muted">
                <span className="font-medium">{u.fullName}</span>
                <span className="amount text-sm text-text-muted">{u.mobile}</span>
                <span className="ml-auto flex flex-wrap gap-1">
                  {u.role === 'ADMIN' && <Badge tone="primary">{t('users.roleAdmin')}</Badge>}
                  {u.status === 'INACTIVE' && <Badge tone="danger">{t('users.statusInactive')}</Badge>}
                  {u.pinMustChange && u.status === 'ACTIVE' && <Badge tone="warning">{t('users.pinPending')}</Badge>}
                </span>
                <span className="w-full text-xs text-text-muted">
                  {u.lastLoginAt ? t('users.lastLogin', { when: formatDateTime(u.lastLoginAt) }) : t('users.neverSignedIn')}
                  {u.role === 'MEMBER' && ` · ${t('users.fundCount', { count: u.fundCount })}`}
                </span>
              </Link>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
