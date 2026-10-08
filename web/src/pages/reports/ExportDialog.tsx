import { useState } from 'react'
import { Link } from 'react-router'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { api } from '../../lib/api/client'
import { ApiError, unwrap } from '../../lib/api/errors'
import { downloadExport, EXPORT_ACTIVE, useExportJob } from '../../lib/api/exports'
import { Button, Dialog, ErrorBanner } from '../../components/ui'
import { toast } from '../../lib/toast'

const FORMATS = ['XLSX', 'CSV', 'PDF'] as const

/** Format sheet → `POST /exports` (with an Idempotency-Key) → progress → Download. */
export function ExportDialog({ open, onClose, code, fundId, period, filters }: {
  open: boolean; onClose: () => void; code: string; fundId: string
  period: { from?: string | undefined; to?: string | undefined }
  filters: { categoryId?: string | undefined; accountId?: string | undefined }
}) {
  const { t } = useTranslation()
  const queryClient = useQueryClient()
  const [format, setFormat] = useState<(typeof FORMATS)[number]>('XLSX')
  const [jobId, setJobId] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  // One key per attempt: a double tap can never start two exports.
  const [key, setKey] = useState(() => crypto.randomUUID())
  const job = useExportJob(jobId)

  const start = useMutation({
    mutationFn: async () => unwrap(await api.POST('/api/v1/exports', {
      params: { header: { 'Idempotency-Key': key } },
      body: { reportCode: code, format, fundId, from: period.from ?? null, to: period.to ?? null, categoryId: filters.categoryId ?? null, accountId: filters.accountId ?? null },
    })),
    onSuccess: async (created) => {
      setJobId(created.id)
      await queryClient.invalidateQueries({ queryKey: ['exports'] })
    },
    onError: (err) => setError(err instanceof ApiError ? err.message : t('errors.generic')),
  })

  const close = () => {
    setJobId(null)
    setError(null)
    setKey(crypto.randomUUID())
    onClose()
  }

  const j = job.data
  const working = !!jobId && (!j || EXPORT_ACTIVE.has(j.status))
  return (
    <Dialog open={open} onClose={close} title={t('reports.export')}
      footer={j?.status === 'SUCCEEDED'
        ? <>
            <Button variant="secondary" onClick={close}>{t('actions.close')}</Button>
            <Button onClick={() => void downloadExport(j).catch((e: unknown) => toast.error(e instanceof ApiError ? e.message : t('errors.generic')))}>{t('reports.download')}</Button>
          </>
        : <>
            <Button variant="secondary" onClick={close}>{t('actions.cancel')}</Button>
            <Button loading={start.isPending || working} disabled={!!jobId} onClick={() => { setError(null); start.mutate() }}>{t('reports.export')}</Button>
          </>}>
      <ErrorBanner message={error ?? (j?.status === 'FAILED' ? t('reports.exportFailed') : null)} />
      {!jobId && (
        <fieldset className="grid gap-2">
          <legend className="text-sm font-medium">{t('reports.format')}</legend>
          <div className="grid grid-cols-3 gap-2" role="radiogroup">
            {FORMATS.map((f) => (
              <label key={f} className={`flex min-h-11 cursor-pointer items-center justify-center rounded-md border text-sm ${format === f ? 'border-primary font-semibold text-primary' : 'border-border'}`}>
                <input type="radio" name="format" value={f} checked={format === f} onChange={() => setFormat(f)} className="sr-only" />
                {t(`reports.formats.${f}`)}
              </label>
            ))}
          </div>
        </fieldset>
      )}
      {working && <p role="status">{t('reports.preparing')}</p>}
      {j?.status === 'SUCCEEDED' && (
        <p role="status">
          {t('reports.ready', { rows: j.rowCount ?? 0 })} <Link to="/reports/exports" className="text-primary underline" onClick={close}>{t('reports.myDownloads')}</Link>
        </p>
      )}
    </Dialog>
  )
}
