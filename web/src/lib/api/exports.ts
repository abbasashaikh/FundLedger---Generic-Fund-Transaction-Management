import { useQuery } from '@tanstack/react-query'
import { api, type Schemas } from './client'
import { ApiError, unwrap } from './errors'
import { fetchExportFile, saveBlob } from './files'

export type ExportJob = Schemas['ExportJobDto']

export const EXPORT_ACTIVE = new Set(['QUEUED', 'RUNNING'])

export const useExportJob = (id: string | null) =>
  useQuery<ExportJob, ApiError>({
    queryKey: ['export', id],
    enabled: !!id,
    // Poll while the file is being built (App Flow §4.9: "Preparing your report…").
    refetchInterval: (q) => (q.state.data && EXPORT_ACTIVE.has(q.state.data.status) ? 1500 : false),
    queryFn: async () => unwrap(await api.GET('/api/v1/exports/{id}', { params: { path: { id: id! } } })),
  })

export async function downloadExport(job: ExportJob): Promise<void> {
  const { blob, name } = await fetchExportFile(job.id)
  saveBlob(blob, name)
}
