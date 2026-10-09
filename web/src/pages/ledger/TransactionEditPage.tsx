import { Link, useParams } from 'react-router'
import { useTranslation } from 'react-i18next'
import { useMe } from '../../lib/auth/queries'
import { ErrorBanner } from '../../components/ui'
import { EntryForm, type EntryKind } from '../money/TransactionFormPage'
import { NotFoundPage } from '../NotFoundPage'
import { useTransactionDetail } from '../../lib/api/hooks'

const KIND: Partial<Record<string, EntryKind>> = { DEPOSIT: 'in', EXPENSE: 'out', TRANSFER: 'transfer' }

/** S07 → Edit: the entry form, pre-filled, for an entry the caller may still change (PRD §11.1). */
export function TransactionEditPage() {
  const { t } = useTranslation()
  const { id } = useParams()
  const me = useMe()
  const detail = useTransactionDetail(id)

  if (detail.isPending || me.isPending) return <p className="text-text-muted">{t('app.loading')}</p>
  if (detail.error?.status === 404) return <NotFoundPage />
  if (detail.isError) return <ErrorBanner message={detail.error.message} />

  const x = detail.data.transaction
  const kind = KIND[x.type]
  const fund = me.data?.funds.find((f) => f.id === x.fundId)
  // Adjustments are corrected by cancelling and recording a new one, which keeps both in the history.
  if (!detail.data.canEdit || !kind || !fund) {
    return (
      <div className="mx-auto max-w-xl py-16 text-center">
        <p>{t('txn.cannotEdit')}</p>
        <Link to={`/txn/${x.id}`} className="mt-4 inline-flex min-h-11 items-center rounded-md bg-primary px-4 font-medium text-primary-fg">{t('txn.backToEntry')}</Link>
      </div>
    )
  }

  // Keyed by revision: if the entry changes underneath, the form starts again from the new values.
  return <EntryForm key={`${x.id}:${x.revision}`} kind={kind} fund={fund} edit={detail.data} />
}
