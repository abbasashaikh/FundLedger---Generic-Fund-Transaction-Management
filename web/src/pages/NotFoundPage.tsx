import { Link } from 'react-router'
import { useTranslation } from 'react-i18next'

// Deliberately does not say whether a record exists (App Flow N-6/N-8).
export function NotFoundPage() {
  const { t } = useTranslation()
  return (
    <section aria-labelledby="page-title" className="mx-auto max-w-xl py-16 text-center">
      <h1 id="page-title" className="text-2xl font-semibold">{t('screens.notFound')}</h1>
      <p className="mt-2 text-text-muted">{t('notFound.body')}</p>
      <Link to="/" className="mt-6 inline-flex min-h-11 items-center rounded-md bg-primary px-4 font-medium text-primary-fg">
        {t('notFound.home')}
      </Link>
    </section>
  )
}
