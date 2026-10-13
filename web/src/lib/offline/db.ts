import Dexie, { type Table } from 'dexie'
import type { Schemas } from '../api/client'

// Offline storage (TRD §8.2, ADR-0005). Three stores from the design plus two small helpers:
//   outbox       queued entries, bound to the user and organization that created them
//   refdata      the user's last-seen reference data, used to fill forms while offline
//   ledgerCache  the last entries seen per fund, for read-only viewing offline
//   meta         the last signed-in profile (no secrets), so the app can open offline
//   discards     "I discarded this rejected entry" notices waiting to be sent to the audit log
// No token, PIN or cookie is ever stored here.

export type OutboxState = 'PENDING' | 'SYNCING' | 'NEEDS_ATTENTION'

export type SyncCommand = Schemas['SyncCommand']

/** Names captured when the entry was queued, so the pending list reads well without reference data. */
export type OutboxDisplay = { fundName: string; category?: string; account?: string; fromAccount?: string; toAccount?: string; paymentMode?: string }

export type OutboxError = { code: string; message: string; fieldErrors?: Record<string, string[]> | null }

export type OutboxItem = {
  clientTxnId: string
  userId: string
  orgId: string
  fundId: string
  command: SyncCommand
  display: OutboxDisplay
  createdAt: string
  state: OutboxState
  attempts: number
  /** Epoch ms before which an automatic retry waits (exponential backoff). */
  nextAttemptAt: number
  lastError?: OutboxError | undefined
}

export type RefRecord = { key: string; userId: string; value: unknown; savedAt: string }
export type LedgerCacheRecord = { key: string; userId: string; fundId: string; items: Schemas['TransactionDto'][]; savedAt: string }
export type MetaRecord = { key: string; value: unknown }
export type DiscardNotice = { clientTxnId: string; userId: string; summary: string }

class FundLedgerOfflineDb extends Dexie {
  outbox!: Table<OutboxItem, string>
  refdata!: Table<RefRecord, string>
  ledgerCache!: Table<LedgerCacheRecord, string>
  meta!: Table<MetaRecord, string>
  discards!: Table<DiscardNotice, string>

  constructor() {
    super('fundledger')
    this.version(1).stores({
      outbox: 'clientTxnId, userId, state, createdAt',
      refdata: 'key, userId',
      ledgerCache: 'key, userId',
      meta: 'key',
      discards: 'clientTxnId, userId',
    })
  }
}

export const offlineDb = new FundLedgerOfflineDb()

/** Removes everything cached for reading (not the outbox: queued entries must survive a sign-out and are bound to their user). */
export async function clearReadCaches(): Promise<void> {
  await Promise.all([offlineDb.refdata.clear(), offlineDb.ledgerCache.clear(), offlineDb.meta.clear()])
}
