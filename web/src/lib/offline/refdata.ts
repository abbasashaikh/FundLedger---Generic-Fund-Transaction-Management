import { ApiError } from '../api/errors'
import type { Schemas } from '../api/client'
import { offlineDb, type LedgerCacheRecord } from './db'

/** True for "the request never reached the server" (offline, DNS, server down), as opposed to an answer with an error. */
export function isNetworkError(error: unknown): boolean {
  return (error instanceof ApiError && error.code === 'NETWORK') || error instanceof TypeError
}

/**
 * Runs <paramref name="fetcher"/> and remembers the result for this user; if the network is unreachable, answers with
 * what was remembered (TRD §8.2 `refdata`). Real server answers, including errors, are never replaced by the cache.
 */
export async function cached<T>(userId: string | undefined, key: string, fetcher: () => Promise<T>): Promise<T> {
  try {
    const value = await fetcher()
    if (userId) void offlineDb.refdata.put({ key: `${userId}:${key}`, userId, value, savedAt: new Date().toISOString() }).catch(() => {})
    return value
  } catch (error) {
    if (userId && isNetworkError(error)) {
      const hit = await offlineDb.refdata.get(`${userId}:${key}`).catch(() => undefined)
      if (hit) return hit.value as T
    }

    throw error
  }
}

const MAX_CACHED = 200

export async function saveLedgerCache(userId: string, fundId: string, items: Schemas['TransactionDto'][]): Promise<void> {
  const record: LedgerCacheRecord = { key: `${userId}:${fundId}`, userId, fundId, items: items.slice(0, MAX_CACHED), savedAt: new Date().toISOString() }
  await offlineDb.ledgerCache.put(record).catch(() => {})
}

export const loadLedgerCache = (userId: string, fundId: string): Promise<LedgerCacheRecord | undefined> =>
  offlineDb.ledgerCache.get(`${userId}:${fundId}`).catch(() => undefined)

// ---- the last signed-in profile (so the app can open with no connection) ---------------------------------------------------
// Holds only the public profile (name, role, ids), never a token. It is removed on sign-out and when the server refuses the session.
export type StoredProfile = Schemas['SessionProfile']

export const saveLastSession = (user: StoredProfile): Promise<unknown> => offlineDb.meta.put({ key: 'lastSession', value: user }).catch(() => {})

export async function loadLastSession(): Promise<StoredProfile | undefined> {
  const row = await offlineDb.meta.get('lastSession').catch(() => undefined)
  return row?.value as StoredProfile | undefined
}
