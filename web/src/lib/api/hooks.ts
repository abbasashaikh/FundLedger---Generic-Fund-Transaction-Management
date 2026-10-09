import { useQuery, type UseQueryResult } from '@tanstack/react-query'
import { useSession } from '../auth/session'
import { cached } from '../offline/refdata'
import { api, type Schemas } from './client'
import { type ApiError, unwrap } from './errors'

export type Account = Schemas['AccountDto']
export type Category = Schemas['CategoryDto']
export type PaymentMode = Schemas['PaymentModeDto']
export type FundType = Schemas['FundTypeDto']
export type Txn = Schemas['TransactionDto']
export type AccountBalance = Schemas['AccountBalanceDto']
export type TxnDetail = Schemas['TransactionDetail']

// Master data changes rarely: keep it fresh for a few minutes. Keys include the fund where the
// answer depends on it, so a cache entry can never be shown under another fund (TRD TR-006).
const MASTER = 5 * 60_000

const useUserId = () => useSession((s) => s.user?.id)

export const useAccounts = (includeInactive = false): UseQueryResult<Account[]> => {
  const userId = useUserId()
  return useQuery({
    queryKey: ['accounts', { includeInactive }],
    staleTime: MASTER,
    queryFn: () => cached(userId, `accounts:${includeInactive}`, async () => unwrap(await api.GET('/api/v1/accounts', { params: { query: { includeInactive } } }))),
  })
}

export const usePaymentModes = (includeInactive = false): UseQueryResult<PaymentMode[]> => {
  const userId = useUserId()
  return useQuery({
    queryKey: ['payment-modes', { includeInactive }],
    staleTime: MASTER,
    queryFn: () => cached(userId, `modes:${includeInactive}`, async () => unwrap(await api.GET('/api/v1/payment-modes', { params: { query: { includeInactive } } }))),
  })
}

export const useFundTypes = (includeInactive = false): UseQueryResult<FundType[]> =>
  useQuery({
    queryKey: ['fund-types', { includeInactive }],
    staleTime: MASTER,
    queryFn: async () => unwrap(await api.GET('/api/v1/fund-types', { params: { query: { includeInactive } } })),
  })

export const useCategories = (
  direction: 'MONEY_IN' | 'MONEY_OUT' | undefined, fundId: string | undefined, includeInactive = false,
): UseQueryResult<Category[]> => {
  const userId = useUserId()
  return useQuery({
    queryKey: ['categories', { direction, fundId, includeInactive }],
    staleTime: MASTER,
    queryFn: () => cached(userId, `categories:${direction ?? 'ALL'}:${fundId ?? '-'}:${includeInactive}`, async () =>
      unwrap(await api.GET('/api/v1/categories', { params: { query: { direction, fundId, includeInactive } } }))),
  })
}

/** Computed per-account balances for one fund (also feeds the transfer pickers). */
export const useAccountBalances = (fundId: string | undefined): UseQueryResult<AccountBalance[]> => {
  const userId = useUserId()
  return useQuery({
    queryKey: ['account-balances', fundId],
    enabled: !!fundId,
    queryFn: () => cached(userId, `balances:${fundId}`, async () => unwrap(await api.GET('/api/v1/accounts/balances', { params: { query: { fundId: fundId! } } }))),
  })
}

export const useDashboard = (fundId: string | undefined) => {
  const userId = useUserId()
  return useQuery({
    queryKey: ['dashboard', fundId],
    enabled: !!fundId,
    queryFn: () => cached(userId, `dashboard:${fundId}`, async () => unwrap(await api.GET('/api/v1/dashboard', { params: { query: { fundId: fundId! } } }))),
  })
}

/** One entry plus what the caller may do with it (edit window, cancel). */
export const useTransactionDetail = (id: string | undefined) =>
  useQuery<TxnDetail, ApiError>({
    queryKey: ['transaction', id],
    enabled: !!id,
    queryFn: async () => unwrap(await api.GET('/api/v1/transactions/{id}', { params: { path: { id: id! } } })),
  })
