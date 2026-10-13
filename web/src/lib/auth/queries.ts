import { useQuery } from '@tanstack/react-query'
import { create } from 'zustand'
import { api, type Schemas } from '../api/client'
import { unwrap } from '../api/errors'
import { useSession } from './session'
import { cached } from '../offline/refdata'

export type Me = Schemas['MeResponse']
export type AccessibleFund = Schemas['AccessibleFund']

/** GET /me — profile, organization and accessible funds with permissions. */
export function useMe() {
  const status = useSession((s) => s.status)
  const userId = useSession((s) => s.user?.id)
  return useQuery({
    queryKey: ['me', userId],
    enabled: status === 'authenticated',
    queryFn: () => cached(userId, 'me', async () => unwrap(await api.GET('/api/v1/me'))),
  })
}

/** The fund the user is working in (App Flow N-1), remembered per user on this device. */
type FundSelection = { byUser: Record<string, string>; select: (userId: string, fundId: string) => void }

const STORAGE_KEY = 'fl.selectedFund'

export const useFundSelection = create<FundSelection>((set) => ({
  byUser: JSON.parse(localStorage.getItem(STORAGE_KEY) ?? '{}') as Record<string, string>,
  select: (userId, fundId) =>
    set((s) => {
      const byUser = { ...s.byUser, [userId]: fundId }
      localStorage.setItem(STORAGE_KEY, JSON.stringify(byUser))
      return { byUser }
    }),
}))

/** The selected fund if still accessible; otherwise the first active one (N-2/N-4). */
export function useSelectedFund(me: Me | undefined): AccessibleFund | undefined {
  const chosen = useFundSelection((s) => (me ? s.byUser[me.id] : undefined))
  if (!me) return undefined
  return me.funds.find((f) => f.id === chosen) ?? me.funds.find((f) => f.status === 'ACTIVE') ?? me.funds[0]
}
