import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { api } from '../../lib/api/client'
import { unwrap } from '../../lib/api/errors'
import { useAccounts, useCategories, useFundTypes, usePaymentModes } from '../../lib/api/hooks'
import { useMe } from '../../lib/auth/queries'
import { MasterData } from './MasterData'

const s = (v: string | boolean | undefined) => String(v ?? '').trim()

/** S22 — accounts (Main Cash, Bank, Cash Counter 1, ...). */
export function AccountsPage() {
  const { t } = useTranslation()
  const accounts = useAccounts(true)
  const kinds: [string, string][] = ['CASH', 'BANK', 'UPI', 'WALLET', 'OTHER'].map((k) => [k, t(`master.kind.${k}`)])
  return (
    <MasterData
      title={t('screens.accounts')} intro={t('master.accountsIntro')} items={accounts.data} loading={accounts.isPending}
      addLabel={t('master.addAccount')} emptyText={t('master.none')} invalidate={[['accounts'], ['account-balances']]}
      fields={[
        { key: 'name', label: t('master.name'), kind: 'text', required: true, maxLength: 80 },
        { key: 'kind', label: t('master.kindLabel'), kind: 'select', options: kinds },
        { key: 'bankName', label: t('master.bankName'), kind: 'text', maxLength: 80 },
        { key: 'accountNumberLast4', label: t('master.last4'), kind: 'text', maxLength: 4, inputMode: 'numeric' },
        { key: 'isActive', label: t('master.active'), kind: 'checkbox' },
      ]}
      blank={{ name: '', kind: 'CASH', bankName: '', accountNumberLast4: '', isActive: true }}
      toValues={(a) => ({ name: a.name, kind: a.kind, bankName: a.bankName ?? '', accountNumberLast4: a.accountNumberLast4 ?? '', isActive: a.isActive })}
      describe={(a) => ({ primary: a.name, secondary: `${t(`master.kind.${a.kind}`)}${a.accountNumberLast4 ? ` ····${a.accountNumberLast4}` : ''}` })}
      save={async (v, id) => {
        const body = { name: s(v.name), kind: s(v.kind) as 'CASH', bankName: s(v.bankName) || null, accountNumberLast4: s(v.accountNumberLast4) || null, isActive: v.isActive === true, sortOrder: 0 }
        return id
          ? unwrap(await api.PUT('/api/v1/accounts/{id}', { params: { path: { id } }, body }))
          : unwrap(await api.POST('/api/v1/accounts', { body }))
      }}
    />
  )
}

/** S23 — categories for Money In / Money Out, optionally scoped to one fund. */
export function CategoriesPage() {
  const { t } = useTranslation()
  const [direction, setDirection] = useState<'MONEY_IN' | 'MONEY_OUT'>('MONEY_IN')
  const categories = useCategories(direction, undefined, true)
  const me = useMe()
  const scope: [string, string][] = [['', t('master.allFunds')], ...(me.data?.funds ?? []).map((f): [string, string] => [f.id, f.name])]
  return (
    <MasterData
      title={t('screens.categories')} intro={t('master.categoriesIntro')} items={categories.data} loading={categories.isPending}
      addLabel={t('master.addCategory')} emptyText={t('master.none')} invalidate={[['categories']]}
      extraHeader={
        <div role="tablist" aria-label={t('master.direction')} className="grid grid-cols-2 gap-2">
          {(['MONEY_IN', 'MONEY_OUT'] as const).map((d) => (
            <button key={d} role="tab" type="button" aria-selected={direction === d} onClick={() => setDirection(d)}
              className={`min-h-11 rounded-md border text-sm ${direction === d ? 'border-primary font-semibold text-primary' : 'border-border'}`}>
              {t(d === 'MONEY_IN' ? 'actions.moneyIn' : 'actions.moneyOut')}
            </button>
          ))}
        </div>
      }
      fields={[
        { key: 'name', label: t('master.name'), kind: 'text', required: true, maxLength: 60 },
        { key: 'fundId', label: t('master.scope'), kind: 'select', options: scope },
        { key: 'isActive', label: t('master.active'), kind: 'checkbox' },
      ]}
      blank={{ name: '', fundId: '', isActive: true }}
      toValues={(c) => ({ name: c.name, fundId: c.fundId ?? '', isActive: c.isActive })}
      describe={(c) => ({ primary: c.name, secondary: c.fundId ? scope.find(([id]) => id === c.fundId)?.[1] : t('master.allFunds') })}
      save={async (v, id) => {
        const fundId = s(v.fundId) || null
        return id
          ? unwrap(await api.PUT('/api/v1/categories/{id}', { params: { path: { id } }, body: { name: s(v.name), icon: null, fundId, isActive: v.isActive === true, sortOrder: 0 } }))
          : unwrap(await api.POST('/api/v1/categories', { body: { direction, name: s(v.name), icon: null, fundId, sortOrder: 0 } }))
      }}
    />
  )
}

/** S24 — payment modes and fund types. */
export function LookupsPage() {
  const { t } = useTranslation()
  const modes = usePaymentModes(true)
  const types = useFundTypes(true)
  return (
    <div className="grid gap-10">
      <MasterData
        title={t('master.paymentModes')} intro={t('master.modesIntro')} items={modes.data} loading={modes.isPending}
        addLabel={t('master.addMode')} emptyText={t('master.none')} invalidate={[['payment-modes']]}
        fields={[
          { key: 'name', label: t('master.name'), kind: 'text', required: true, maxLength: 40 },
          { key: 'requiresReference', label: t('master.requiresReference'), kind: 'checkbox' },
          { key: 'isActive', label: t('master.active'), kind: 'checkbox' },
        ]}
        blank={{ name: '', requiresReference: false, isActive: true }}
        toValues={(m) => ({ name: m.name, requiresReference: m.requiresReference, isActive: m.isActive })}
        describe={(m) => ({ primary: m.name, secondary: m.requiresReference ? t('master.needsRef') : undefined })}
        save={async (v, id) => {
          const body = { name: s(v.name), requiresReference: v.requiresReference === true, isActive: v.isActive === true, sortOrder: 0 }
          return id
            ? unwrap(await api.PUT('/api/v1/payment-modes/{id}', { params: { path: { id } }, body }))
            : unwrap(await api.POST('/api/v1/payment-modes', { body }))
        }}
      />
      <MasterData
        title={t('master.fundTypes')} items={types.data} loading={types.isPending}
        addLabel={t('master.addFundType')} emptyText={t('master.none')} invalidate={[['fund-types']]}
        fields={[
          { key: 'name', label: t('master.name'), kind: 'text', required: true, maxLength: 60 },
          { key: 'isActive', label: t('master.active'), kind: 'checkbox' },
        ]}
        blank={{ name: '', isActive: true }}
        toValues={(f) => ({ name: f.name, isActive: f.isActive })}
        describe={(f) => ({ primary: f.name })}
        save={async (v, id) => {
          const body = { name: s(v.name), isActive: v.isActive === true, sortOrder: 0 }
          return id
            ? unwrap(await api.PUT('/api/v1/fund-types/{id}', { params: { path: { id } }, body }))
            : unwrap(await api.POST('/api/v1/fund-types', { body }))
        }}
      />
    </div>
  )
}
