import { useQuery } from '@tanstack/react-query'
import { api, API_BASE_URL, type Schemas } from './client'
import { ApiError, toApiError, unwrap } from './errors'
import { authFetch, CLIENT_HEADER } from '../auth/session'

export type Attachment = Schemas['AttachmentDto']

/** Most files per entry (TR-050), and how many can wait on the device while offline (TR-055). */
export const MAX_FILES = 5
export const MAX_OFFLINE_FILES = 3

/** Photos are shrunk on the phone before upload (TR-052): at most this many pixels on the long edge. */
const LONG_EDGE = 1600
const QUALITY = 0.8

export function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

export const useAttachments = (transactionId: string | undefined) =>
  useQuery<Attachment[], ApiError>({
    queryKey: ['attachments', transactionId],
    enabled: !!transactionId,
    queryFn: async () => unwrap(await api.GET('/api/v1/transactions/{id}/attachments', { params: { path: { id: transactionId! } } })),
  })

/**
 * Makes a photo small enough for a weak connection: scaled to 1600 px and saved as JPEG at 0.8 (TR-052). PDFs, files the
 * browser can't draw, and photos that are already small pass through unchanged. The server re-encodes everything anyway;
 * this is only about the upload size.
 */
export async function prepareFile(file: File): Promise<File> {
  if (!file.type.startsWith('image/') || file.type === 'image/gif') return file
  try {
    const bitmap = await createImageBitmap(file, { imageOrientation: 'from-image' })
    const scale = Math.min(1, LONG_EDGE / Math.max(bitmap.width, bitmap.height))
    if (scale === 1 && file.size < 400_000) {
      bitmap.close()
      return file
    }

    const canvas = document.createElement('canvas')
    canvas.width = Math.max(1, Math.round(bitmap.width * scale))
    canvas.height = Math.max(1, Math.round(bitmap.height * scale))
    canvas.getContext('2d')!.drawImage(bitmap, 0, 0, canvas.width, canvas.height)
    bitmap.close()
    const blob = await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, 'image/jpeg', QUALITY))
    if (!blob || blob.size >= file.size) return file
    return new File([blob], file.name.replace(/\.[^.]+$/, '') + '.jpg', { type: 'image/jpeg' })
  } catch {
    return file
  }
}

/** Uploads one file to an entry. Throws ApiError with the server's message (too big, wrong type, limit reached, ...). */
export async function uploadAttachment(transactionId: string, file: Blob, fileName: string): Promise<Attachment> {
  const form = new FormData()
  form.append('file', file, fileName)
  let response: Response
  try {
    response = await authFetch(new Request(`${API_BASE_URL}/api/v1/transactions/${transactionId}/attachments`, {
      method: 'POST', body: form, credentials: 'include', headers: { ...CLIENT_HEADER },
    }))
  } catch {
    throw ApiError.network()
  }

  if (!response.ok) throw await toApiError(response)
  return (await response.json()) as Attachment
}

/** Fetches the file with the user's credentials (a plain <img src> can't send them). The server may redirect to a 60-second link. */
export async function fetchAttachmentBlob(id: string): Promise<Blob> {
  const result = await api.GET('/api/v1/attachments/{id}', { params: { path: { id } }, parseAs: 'blob' })
  return unwrap(result) as unknown as Blob
}

/** Statuses that mean "this file will never be accepted as it is" (so the offline queue gives up on it rather than retrying). */
export const isPermanentUploadError = (error: unknown): boolean =>
  error instanceof ApiError && [400, 403, 404, 409, 413, 415].includes(error.status)
