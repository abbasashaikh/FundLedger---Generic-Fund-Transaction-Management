import { useState, type ReactNode } from 'react'
import { useMutation, useQueryClient, type QueryKey } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Plus } from 'lucide-react'
import { ApiError, type FieldErrors } from '../../lib/api/errors'
import { Badge, Button, Checkbox, Dialog, ErrorBanner, Field } from '../../components/ui'
import { toast } from '../../lib/toast'

export type FieldSpec = {
  key: string
  label: string
  kind: 'text' | 'checkbox' | 'select'
  options?: [string, string][]
  required?: boolean
  maxLength?: number
  /** Shown only when creating (e.g. a category's direction can't change afterwards). */
  createOnly?: boolean
  inputMode?: 'numeric'
}

type Values = Record<string, string | boolean>
type Item = { id: string; isActive: boolean }

/**
 * Generic list + dialog editor for org master data (accounts, categories, payment modes, fund
 * types). Nothing is ever deleted: items are deactivated, so history keeps its labels
 * (App Flow §5.3). Server field errors are shown next to the field.
 */
export function MasterData<T extends Item>({ title, intro, items, loading, fields, blank, toValues, describe, save, invalidate, addLabel, emptyText, extraHeader }: {
  title: string
  intro?: string
  items: T[] | undefined
  loading: boolean
  fields: FieldSpec[]
  blank: Values
  toValues: (item: T) => Values
  describe: (item: T) => { primary: string; secondary?: string }
  save: (values: Values, id: string | undefined) => Promise<unknown>
  invalidate: QueryKey[]
  addLabel: string
  emptyText: string
  extraHeader?: ReactNode
}) {
  const { t } = useTranslation()
  const queryClient = useQueryClient()
  const [editing, setEditing] = useState<{ id?: string; values: Values } | null>(null)
  const [fieldErrors, setFieldErrors] = useState<FieldErrors>({})
  const [error, setError] = useState<string | null>(null)

  const mutation = useMutation({
    mutationFn: async () => save(editing!.values, editing!.id),
    onSuccess: async () => {
      setEditing(null)
      toast.success(t('master.saved'))
      await Promise.all(invalidate.map((queryKey) => queryClient.invalidateQueries({ queryKey })))
    },
    onError: (e) => {
      if (e instanceof ApiError) { setFieldErrors(e.fieldErrors); setError(e.message) } else setError(t('errors.generic'))
    },
  })

  const open = (id: string | undefined, values: Values) => { setFieldErrors({}); setError(null); setEditing({ id, values }) }
  const set = (key: string, v: string | boolean) => setEditing((e) => (e ? { ...e, values: { ...e.values, [key]: v } } : e))

  return (
    <div className="mx-auto grid max-w-3xl gap-4">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="text-2xl font-semibold">{title}</h1>
        <Button className="ml-auto" onClick={() => open(undefined, blank)}><Plus aria-hidden className="size-4" />{addLabel}</Button>
      </div>
      {intro && <p className="text-sm text-text-muted">{intro}</p>}
      {extraHeader}

      {loading && <p className="text-text-muted">{t('app.loading')}</p>}
      {items && items.length === 0 && <p className="py-8 text-center text-text-muted">{emptyText}</p>}
      <ul className="grid gap-2" aria-label={title}>
        {items?.map((item) => {
          const d = describe(item)
          return (
            <li key={item.id}>
              <button type="button" onClick={() => open(item.id, toValues(item))}
                className={`flex min-h-14 w-full flex-wrap items-center gap-2 rounded-md border border-border bg-surface px-4 py-2 text-left hover:bg-surface-muted ${item.isActive ? '' : 'opacity-60'}`}>
                <span className="font-medium">{d.primary}</span>
                {d.secondary && <span className="text-sm text-text-muted">{d.secondary}</span>}
                {!item.isActive && <Badge tone="danger">{t('master.inactive')}</Badge>}
                <span className="ml-auto text-sm text-primary">{t('master.edit')}</span>
              </button>
            </li>
          )
        })}
      </ul>

      <Dialog open={!!editing} onClose={() => setEditing(null)} title={editing?.id ? t('master.editTitle', { name: title }) : addLabel}
        footer={<>
          <Button variant="secondary" onClick={() => setEditing(null)}>{t('actions.cancel')}</Button>
          <Button loading={mutation.isPending} onClick={() => mutation.mutate()}>{t('actions.save')}</Button>
        </>}>
        <ErrorBanner message={error} />
        {editing && fields.filter((f) => !f.createOnly || !editing.id).map((f) => {
          const v = editing.values[f.key]
          if (f.kind === 'checkbox') return <Checkbox key={f.key} label={f.label} checked={v === true} onChange={(c) => set(f.key, c)} />
          if (f.kind === 'select') {
            return (
              <div key={f.key} className="grid gap-1">
                <label htmlFor={`m-${f.key}`} className="text-sm font-medium">{f.label}</label>
                <select id={`m-${f.key}`} value={String(v ?? '')} onChange={(e) => set(f.key, e.target.value)} className="min-h-11 rounded-md border border-border bg-surface px-3">
                  {f.options?.map(([value, label]) => <option key={value} value={value}>{label}</option>)}
                </select>
                {fieldErrors[f.key]?.[0] && <p role="alert" className="text-sm text-danger">{fieldErrors[f.key]![0]}</p>}
              </div>
            )
          }
          return <Field key={f.key} label={f.label} required={f.required} maxLength={f.maxLength} inputMode={f.inputMode} value={String(v ?? '')}
            onChange={(e) => set(f.key, e.target.value)} error={fieldErrors[f.key]?.[0]} />
        })}
      </Dialog>
    </div>
  )
}
