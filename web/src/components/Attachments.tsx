import { useEffect, useMemo, useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Camera, EyeOff, FileText, Paperclip, X } from 'lucide-react'
import { api } from '../lib/api/client'
import { ApiError, unwrap } from '../lib/api/errors'
import { fetchAttachmentBlob, formatSize, prepareFile, uploadAttachment, useAttachments, type Attachment } from '../lib/api/attachments'
import { Badge, Button, Dialog, ErrorBanner } from './ui'
import { toast } from '../lib/toast'

const ACCEPT = 'image/jpeg,image/png,image/webp,application/pdf'

/**
 * Chooses photos or PDFs for an entry that is being recorded. Photos are shrunk on the phone first (TR-052).
 * The files themselves are uploaded after the entry is saved (or after it syncs, when offline).
 */
export function FilePicker({ files, onChange, max, disabled }: { files: File[]; onChange: (files: File[]) => void; max: number; disabled?: boolean }) {
  const { t } = useTranslation()
  const [busy, setBusy] = useState(false)
  const camera = useRef<HTMLInputElement>(null)
  const chooser = useRef<HTMLInputElement>(null)

  async function add(list: FileList | null) {
    if (!list || list.length === 0) return
    setBusy(true)
    try {
      const prepared = await Promise.all(Array.from(list).map(prepareFile))
      onChange([...files, ...prepared].slice(0, max))
      if (files.length + prepared.length > max) toast.error(t('attachments.tooMany', { max }))
    } finally {
      setBusy(false)
      if (camera.current) camera.current.value = ''
      if (chooser.current) chooser.current.value = ''
    }
  }

  return (
    <div className="grid gap-2">
      <p className="text-sm font-medium">{t('attachments.title')}</p>
      <div className="flex flex-wrap gap-2">
        <input ref={camera} type="file" accept="image/*" capture="environment" className="sr-only" aria-label={t('attachments.takePhoto')} tabIndex={-1} onChange={(e) => void add(e.target.files)} />
        <input ref={chooser} type="file" accept={ACCEPT} multiple className="sr-only" aria-label={t('attachments.chooseFile')} tabIndex={-1} onChange={(e) => void add(e.target.files)} />
        <Button variant="secondary" disabled={disabled || busy || files.length >= max} onClick={() => camera.current?.click()}><Camera aria-hidden className="size-4" />{t('attachments.takePhoto')}</Button>
        <Button variant="secondary" disabled={disabled || busy || files.length >= max} onClick={() => chooser.current?.click()}><Paperclip aria-hidden className="size-4" />{t('attachments.chooseFile')}</Button>
      </div>
      {files.length > 0 && (
        <ul className="grid gap-1" aria-label={t('attachments.chosen')}>
          {files.map((f, i) => (
            <li key={`${f.name}-${i}`} className="flex min-h-11 items-center gap-2 rounded-md border border-border px-3 text-sm">
              <FileText aria-hidden className="size-4 shrink-0 text-text-muted" />
              <span className="min-w-0 flex-1 truncate">{f.name}</span>
              <span className="text-xs text-text-muted">{formatSize(f.size)}</span>
              <button type="button" aria-label={t('attachments.remove', { name: f.name })} className="flex size-9 items-center justify-center rounded-md hover:bg-surface-muted" onClick={() => onChange(files.filter((_, j) => j !== i))}>
                <X aria-hidden className="size-4" />
              </button>
            </li>
          ))}
        </ul>
      )}
      <p className="text-xs text-text-muted">{t('attachments.hint', { max })}</p>
    </div>
  )
}

/** The files on an entry (detail screen): open, add more, and (Admin) hide with a reason. */
export function AttachmentStrip({ transactionId, canAdd, isAdmin, online }: { transactionId: string; canAdd: boolean; isAdmin: boolean; online: boolean }) {
  const { t } = useTranslation()
  const queryClient = useQueryClient()
  const list = useAttachments(transactionId)
  const [viewing, setViewing] = useState<Attachment | null>(null)
  const [hiding, setHiding] = useState<Attachment | null>(null)
  const picker = useRef<HTMLInputElement>(null)
  const shown = list.data ?? []
  const visible = shown.filter((a) => !a.isHidden).length

  const upload = useMutation({
    mutationFn: async (files: FileList) => {
      for (const original of Array.from(files)) {
        const file = await prepareFile(original)
        await uploadAttachment(transactionId, file, file.name)
      }
    },
    onSuccess: () => toast.success(t('attachments.added')),
    onError: (err) => toast.error(err instanceof ApiError ? err.message : t('errors.generic')),
    onSettled: async () => queryClient.invalidateQueries({ queryKey: ['attachments', transactionId] }),
  })

  if (list.isError && list.error.status === 404) return null
  if (shown.length === 0 && !canAdd) return null

  return (
    <section aria-labelledby="files-h" className="grid gap-3 rounded-lg border border-border bg-surface p-4">
      <div className="flex flex-wrap items-center gap-2">
        <h2 id="files-h" className="font-semibold">{t('attachments.title')}</h2>
        {canAdd && visible < 5 && (
          <>
            <input ref={picker} type="file" accept={`${ACCEPT},image/*`} multiple className="sr-only" aria-label={t('attachments.add')} tabIndex={-1}
              onChange={(e) => { if (e.target.files?.length) upload.mutate(e.target.files); e.target.value = '' }} />
            <Button variant="secondary" className="ml-auto" loading={upload.isPending} disabled={!online} title={online ? undefined : t('txn.needsInternet')} onClick={() => picker.current?.click()}>
              <Paperclip aria-hidden className="size-4" />{t('attachments.add')}
            </Button>
          </>
        )}
      </div>
      <ErrorBanner message={list.error?.message} />
      {shown.length === 0 && <p className="text-sm text-text-muted">{t('attachments.none')}</p>}
      <ul className="grid gap-2 sm:grid-cols-2">
        {shown.map((a) => (
          <li key={a.id} className={`flex items-center gap-3 rounded-md border border-border p-2 ${a.isHidden ? 'opacity-60' : ''}`}>
            <Thumb attachment={a} />
            <button type="button" className="min-w-0 flex-1 text-left" onClick={() => setViewing(a)}>
              <span className="block truncate text-sm font-medium">{a.fileName}</span>
              <span className="block text-xs text-text-muted">{formatSize(a.sizeBytes)} · {a.uploadedBy.name}</span>
              {a.isHidden && <Badge tone="warning">{t('attachments.hidden')}</Badge>}
            </button>
            {isAdmin && !a.isHidden && (
              <button type="button" aria-label={t('attachments.hide')} title={t('attachments.hide')} className="flex size-9 items-center justify-center rounded-md hover:bg-surface-muted" onClick={() => setHiding(a)}>
                <EyeOff aria-hidden className="size-4" />
              </button>
            )}
          </li>
        ))}
      </ul>
      <Viewer attachment={viewing} onClose={() => setViewing(null)} />
      <HideDialog attachment={hiding} onClose={() => setHiding(null)} transactionId={transactionId} />
    </section>
  )
}

/** Fetches a file with the user's credentials and gives back a temporary browser URL for it. */
function useBlobUrl(attachment: Attachment | null, enabled = true): string | null {
  const blob = useQuery({
    queryKey: ['attachment-file', attachment?.id],
    enabled: !!attachment && enabled,
    staleTime: 5 * 60_000,
    queryFn: () => fetchAttachmentBlob(attachment!.id),
  })
  const url = useMemo(() => (blob.data ? URL.createObjectURL(blob.data) : null), [blob.data])
  useEffect(() => () => { if (url) URL.revokeObjectURL(url) }, [url])
  return url
}

function Thumb({ attachment }: { attachment: Attachment }) {
  const isImage = attachment.contentType.startsWith('image/')
  const url = useBlobUrl(attachment, isImage)
  return (
    <span className="flex size-14 shrink-0 items-center justify-center overflow-hidden rounded-md bg-surface-muted">
      {isImage && url ? <img src={url} alt="" className="size-full object-cover" /> : <FileText aria-hidden className="size-6 text-text-muted" />}
    </span>
  )
}

function Viewer({ attachment, onClose }: { attachment: Attachment | null; onClose: () => void }) {
  const { t } = useTranslation()
  const url = useBlobUrl(attachment)
  const isImage = attachment?.contentType.startsWith('image/') ?? false
  return (
    <Dialog open={!!attachment} onClose={onClose} title={attachment?.fileName ?? ''}
      footer={<>
        {url && !isImage && <a href={url} target="_blank" rel="noreferrer" className="inline-flex min-h-11 items-center rounded-md bg-primary px-4 font-medium text-primary-fg">{t('attachments.openPdf')}</a>}
        <Button variant="secondary" onClick={onClose}>{t('actions.close')}</Button>
      </>}>
      {!url && <p className="text-text-muted">{t('app.loading')}</p>}
      {url && isImage && <img src={url} alt={attachment?.fileName} className="max-h-[70vh] w-full object-contain" />}
      {url && !isImage && <p className="text-sm text-text-muted">{t('attachments.pdfNote')}</p>}
    </Dialog>
  )
}

function HideDialog({ attachment, onClose, transactionId }: { attachment: Attachment | null; onClose: () => void; transactionId: string }) {
  const { t } = useTranslation()
  const queryClient = useQueryClient()
  const [reason, setReason] = useState('')
  const [error, setError] = useState<string | null>(null)
  const close = () => { setReason(''); setError(null); onClose() }

  const hide = useMutation({
    mutationFn: async () => unwrap(await api.POST('/api/v1/attachments/{id}/hide', { params: { path: { id: attachment!.id } }, body: { reason: reason.trim() } })),
    onSuccess: async () => {
      toast.success(t('attachments.hiddenToast'))
      close()
      await queryClient.invalidateQueries({ queryKey: ['attachments', transactionId] })
    },
    onError: (err) => setError(err instanceof ApiError ? err.message : t('errors.generic')),
  })

  return (
    <Dialog open={!!attachment} onClose={close} title={t('attachments.hideTitle')}
      footer={<>
        <Button variant="secondary" onClick={close}>{t('actions.cancel')}</Button>
        <Button variant="destructive" loading={hide.isPending} disabled={reason.trim().length < 5} onClick={() => hide.mutate()}>{t('attachments.hide')}</Button>
      </>}>
      <ErrorBanner message={error} />
      <p className="text-sm">{t('attachments.hideBody')}</p>
      <label className="grid gap-1 text-sm font-medium">
        {t('attachments.hideReason')}
        <textarea value={reason} onChange={(e) => setReason(e.target.value)} rows={3} maxLength={200} className="rounded-md border border-border bg-surface p-2 font-normal" />
      </label>
    </Dialog>
  )
}
