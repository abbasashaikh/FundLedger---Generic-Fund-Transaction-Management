import { Link } from 'react-router'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { ArrowLeft, Download } from 'lucide-react'
import { api, type Schemas } from '../../lib/api/client'
import { ApiError, unwrap } from '../../lib/api/errors'
import { useMe, useSelectedFund } from '../../lib/auth/queries'
import { formatDateTime } from '../../lib/format/date'
import { Badge, Button, ErrorBanner } from '../../components/ui'
import { toast } from '../../lib/toast'
import { downloadExport } from '../../lib/api/exports'

type Job = Schemas['ExportJobDto']

const TONE = { QUEUED: 'muted', RUNNING: 'muted', SUCCEEDED: 'success', FAILED: 'danger', EXPIRED: 'muted' } as const

/** S16 — my downloads: the caller's own exports for 24 hours (App Flow §4.9). */
export function ExportsPage() {
  const { t } = useTranslation()
  const me = useMe()
  const fund = useSelectedFund(me.data)
  const jobs = useQuery<Job[], ApiError>({
    queryKey: ['exports'],
    refetchInterval: (q) => (q.state.data?.some((j) => j.status === 'QUEUED' || j.status === 'RUNNING') ? 2000 : false),
    queryFn: async () => unwrap(await api.GET('/api/v1/exports')),
  })

  if (me.isPending) return <p className="text-text-muted">{t('app.loading')}</p>
  if (fund && !fund.permissions.export) return <p className="py-16 text-center">{t('reports.noAccess')}</p>

  return (
    <div className="mx-auto grid max-w-3xl gap-4">
      <div className="flex items-center gap-2">
        <Link to="/reports" aria-label={t('screens.reports')} className="flex size-11 items-center justify-center rounded-md hover:bg-surface-muted"><ArrowLeft aria-hidden className="size-5" /></Link>
        <h1 className="text-xl font-semibold">{t('reports.myDownloads')}</h1>
      </div>
      <p className="text-sm text-text-muted">{t('reports.expiryNote')}</p>
      <ErrorBanner message={jobs.error?.message} />
      {jobs.isSuccess && jobs.data.length === 0 && <p className="py-8 text-center text-text-muted">{t('reports.noExports')}</p>}
      <ul className="grid gap-2">
        {(jobs.data ?? []).map((j) => (
          <li key={j.id} className={`flex flex-wrap items-center gap-3 rounded-lg border border-border bg-surface p-3 ${j.status === 'EXPIRED' ? 'opacity-60' : ''}`}>
            <div className="min-w-0 flex-1">
              <p className="font-medium">{j.reportCode.replaceAll('_', ' ')} · {j.format}</p>
              <p className="text-xs text-text-muted">{formatDateTime(j.createdAt)}{j.rowCount != null && ` · ${t('reports.rows', { count: j.rowCount })}`}</p>
            </div>
            <Badge tone={TONE[j.status as keyof typeof TONE] ?? 'muted'}>{t(`reports.status.${j.status}`)}</Badge>
            {j.status === 'SUCCEEDED' && (
              <Button variant="secondary" onClick={() => void downloadExport(j).catch((e: unknown) => toast.error(e instanceof ApiError ? e.message : t('errors.generic')))}>
                <Download aria-hidden className="size-4" />{t('reports.download')}
              </Button>
            )}
          </li>
        ))}
      </ul>
    </div>
  )
}
