import { useTranslation } from 'react-i18next'
import { Hammer } from 'lucide-react'

/** Stand-in for screens built in later phases; keeps every route navigable. */
export function PlaceholderPage({ screen, phase }: { screen: string; phase: string }) {
  const { t } = useTranslation()
  const title = t(`screens.${screen}`)
  return (
    <section aria-labelledby="page-title" className="mx-auto max-w-xl py-16 text-center">
      <Hammer aria-hidden className="mx-auto mb-4 size-10 text-text-muted" />
      <h1 id="page-title" className="text-2xl font-semibold">{title}</h1>
      <p className="mt-2 text-text-muted">{t('placeholder.body', { phase })}</p>
    </section>
  )
}
