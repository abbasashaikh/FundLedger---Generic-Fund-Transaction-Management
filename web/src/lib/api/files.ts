import { api } from './client'
import { unwrap } from './errors'

/** Hands a blob to the browser as a download (no navigation, no URL that outlives the click). */
export function saveBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = fileName
  document.body.appendChild(a)
  a.click()
  a.remove()
  setTimeout(() => URL.revokeObjectURL(url), 10_000)
}

/** File name from a Content-Disposition header (filename="…"), or the fallback. */
export function fileNameFrom(response: Response, fallback: string): string {
  const header = response.headers.get('Content-Disposition') ?? ''
  const star = /filename\*=UTF-8''([^;]+)/i.exec(header)?.[1]
  if (star) return decodeURIComponent(star)
  return /filename="?([^";]+)"?/i.exec(header)?.[1] ?? fallback
}

export async function fetchExportFile(id: string): Promise<{ blob: Blob; name: string }> {
  const result = await api.GET('/api/v1/exports/{id}/file', { params: { path: { id } }, parseAs: 'blob' })
  return { blob: unwrap(result) as unknown as Blob, name: fileNameFrom(result.response, 'export') }
}

export async function fetchReceipt(id: string): Promise<{ blob: Blob; name: string }> {
  const result = await api.GET('/api/v1/transactions/{id}/receipt.pdf', { params: { path: { id } }, parseAs: 'blob' })
  return { blob: unwrap(result) as unknown as Blob, name: fileNameFrom(result.response, 'receipt.pdf') }
}

/**
 * Shares a file through the phone's share sheet (WhatsApp, SMS, email…) when the browser can,
 * otherwise downloads it (ADR-0007). Returns how it was delivered.
 */
export async function shareOrDownload(blob: Blob, name: string, title: string): Promise<'shared' | 'downloaded' | 'cancelled'> {
  const file = new File([blob], name, { type: blob.type || 'application/pdf' })
  if (typeof navigator.canShare === 'function' && navigator.canShare({ files: [file] })) {
    try {
      await navigator.share({ files: [file], title })
      return 'shared'
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') return 'cancelled'
      // Any other failure: fall back to a plain download below.
    }
  }

  saveBlob(blob, name)
  return 'downloaded'
}
