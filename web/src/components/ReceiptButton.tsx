import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { FileText, Share2 } from 'lucide-react'
import { ApiError } from '../lib/api/errors'
import { fetchReceipt, saveBlob, shareOrDownload } from '../lib/api/files'
import { Button } from './ui'
import { toast } from '../lib/toast'

/**
 * Money In receipt (ADR-0007): "Share receipt" opens the phone's share sheet with the PDF; elsewhere (or with
 * `download`) it saves the file. The receipt is always built by the server from the current revision.
 */
export function ReceiptButton({ id, mode = 'share', variant = 'secondary' }: { id: string; mode?: 'share' | 'download'; variant?: 'primary' | 'secondary' | 'ghost' }) {
  const { t } = useTranslation()
  const [busy, setBusy] = useState(false)

  async function run() {
    setBusy(true)
    try {
      const { blob, name } = await fetchReceipt(id)
      if (mode === 'download') {
        saveBlob(blob, name)
      } else {
        const how = await shareOrDownload(blob, name, t('receipt.shareTitle'))
        if (how === 'downloaded') toast.success(t('receipt.downloaded'))
      }
    } catch (err) {
      toast.error(err instanceof ApiError ? err.message : t('errors.generic'))
    } finally {
      setBusy(false)
    }
  }

  const Icon = mode === 'share' ? Share2 : FileText
  return (
    <Button variant={variant} loading={busy} onClick={() => void run()}>
      <Icon aria-hidden className="size-4" />{t(mode === 'share' ? 'receipt.share' : 'receipt.download')}
    </Button>
  )
}
