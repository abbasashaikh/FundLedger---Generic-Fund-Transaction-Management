import { useState } from 'react'
import { Link } from 'react-router'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { api } from '../lib/api/client'
import { readThemePreference, saveThemePreference, type ThemePreference } from '../lib/theme'

// More (S17): theme, admin links on mobile, and System info — which also proves
// the PWA → API connection end to end in each environment.
export function MorePage() {
  const { t } = useTranslation()
  const [theme, setTheme] = useState<ThemePreference>(readThemePreference)

  const version = useQuery({
    queryKey: ['system', 'version'],
    queryFn: async () => {
      const { data, error } = await api.GET('/api/v1/version')
      if (error || !data) throw new Error('unreachable')
      return data
    },
  })

  const choose = (pref: ThemePreference) => {
    setTheme(pref)
    saveThemePreference(pref)
  }

  const admin = [
    ['/admin/users', 'nav.users'], ['/admin/funds', 'nav.funds'], ['/admin/accounts', 'nav.accounts'],
    ['/admin/categories', 'nav.categories'], ['/admin/lookups', 'nav.lookups'], ['/admin/audit', 'nav.audit'],
    ['/admin/settings', 'nav.settings'],
  ] as const

  return (
    <div className="mx-auto grid max-w-xl gap-6">
      <h1 className="text-2xl font-semibold">{t('screens.more')}</h1>

      <fieldset className="rounded-lg border border-border bg-surface p-4">
        <legend className="px-1 font-semibold">{t('theme.label')}</legend>
        <div className="mt-2 grid grid-cols-3 gap-2" role="radiogroup">
          {(['system', 'light', 'dark'] as const).map((pref) => (
            <label key={pref} className={`flex min-h-11 cursor-pointer items-center justify-center rounded-md border text-sm ${theme === pref ? 'border-primary font-semibold text-primary' : 'border-border'}`}>
              <input type="radio" name="theme" value={pref} checked={theme === pref} onChange={() => choose(pref)} className="sr-only" />
              {t(`theme.${pref}`)}
            </label>
          ))}
        </div>
      </fieldset>

      <section className="rounded-lg border border-border bg-surface p-4 lg:hidden" aria-labelledby="admin-heading">
        <h2 id="admin-heading" className="font-semibold">{t('nav.administration')}</h2>
        <ul className="mt-2 grid">
          {admin.map(([to, key]) => (
            <li key={to}>
              <Link to={to} className="flex min-h-11 items-center rounded-md px-2 hover:bg-surface-muted">{t(key)}</Link>
            </li>
          ))}
        </ul>
      </section>

      <section className="rounded-lg border border-border bg-surface p-4" aria-labelledby="system-heading">
        <h2 id="system-heading" className="font-semibold">{t('system.title')}</h2>
        {version.isPending && <p className="mt-2 text-text-muted">{t('system.checking')}</p>}
        {version.isError && <p className="mt-2 text-danger">{t('system.apiUnreachable')}</p>}
        {version.data && (
          <dl className="mt-2 grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm">
            <dt className="text-text-muted">{t('system.apiVersion')}</dt>
            <dd>{version.data.version} ({version.data.commit.slice(0, 7)})</dd>
            <dt className="text-text-muted">{t('system.apiEnvironment')}</dt>
            <dd>{version.data.environment}</dd>
          </dl>
        )}
      </section>
    </div>
  )
}
