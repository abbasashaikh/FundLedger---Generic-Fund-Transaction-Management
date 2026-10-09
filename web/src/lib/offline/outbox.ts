import { liveQuery } from 'dexie'
import { useEffect, useState } from 'react'
import { offlineDb, type OutboxDisplay, type OutboxItem, type SyncCommand } from './db'

/** The signed-in user's identity as the outbox needs it. */
export type Owner = { userId: string; orgId: string }

/** Queues a new entry (state PENDING). The id is the idempotency key the server will see (BR-018). */
export async function enqueue(owner: Owner, command: SyncCommand, display: OutboxDisplay): Promise<OutboxItem> {
  const item: OutboxItem = {
    clientTxnId: command.clientTxnId, userId: owner.userId, orgId: owner.orgId, fundId: command.fundId, command, display,
    createdAt: command.clientCreatedAt, state: 'PENDING', attempts: 0, nextAttemptAt: 0,
  }
  await offlineDb.outbox.put(item)
  return item
}

/** The user's queued entries, oldest first. Entries of other users are never returned (TRD §6.3). */
export const listFor = (userId: string): Promise<OutboxItem[]> => offlineDb.outbox.where('userId').equals(userId).sortBy('createdAt')

export const getItem = (clientTxnId: string): Promise<OutboxItem | undefined> => offlineDb.outbox.get(clientTxnId)

/** After the user fixes a rejected entry: replace its content and send it again (same id: it was never recorded). */
export async function resubmit(clientTxnId: string, command: SyncCommand, display: OutboxDisplay): Promise<void> {
  await offlineDb.outbox.update(clientTxnId, { command, display, fundId: command.fundId, state: 'PENDING', attempts: 0, nextAttemptAt: 0, lastError: undefined })
}

/** Removes a rejected entry the user chose to discard, and remembers to tell the audit log when next online. */
export async function discard(item: OutboxItem, summary: string): Promise<void> {
  await offlineDb.transaction('rw', offlineDb.outbox, offlineDb.discards, async () => {
    await offlineDb.outbox.delete(item.clientTxnId)
    await offlineDb.discards.put({ clientTxnId: item.clientTxnId, userId: item.userId, summary })
  })
}

/** A live view of the user's outbox: re-renders whenever an entry is added, changed or removed (also from another tab). */
const NONE: OutboxItem[] = []

export function useOutbox(userId: string | undefined): OutboxItem[] {
  const [items, setItems] = useState<OutboxItem[]>(NONE)
  useEffect(() => {
    if (!userId) return
    const sub = liveQuery(() => listFor(userId)).subscribe({ next: setItems, error: () => setItems(NONE) })
    return () => sub.unsubscribe()
  }, [userId])
  return userId ? items : NONE
}

/** Plain-language summary of one queued entry ("Collection · Main Cash", "Main Cash → Bank"). */
export function entryTitle(i: OutboxItem): string {
  const d = i.display
  if (i.command.type === 'TRANSFER') return `${d.fromAccount ?? ''} → ${d.toAccount ?? ''}`
  return [d.category, d.account].filter(Boolean).join(' · ')
}

export type OutboxCounts = { pending: number; needsAttention: number; total: number }

export function countOutbox(items: OutboxItem[]): OutboxCounts {
  const needsAttention = items.filter((i) => i.state === 'NEEDS_ATTENTION').length
  return { pending: items.length - needsAttention, needsAttention, total: items.length }
}
