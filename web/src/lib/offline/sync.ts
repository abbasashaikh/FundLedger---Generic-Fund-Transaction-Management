import { create } from 'zustand'
import { api } from '../api/client'
import { ApiError } from '../api/errors'
import { refresh, useSession } from '../auth/session'
import { offlineDb, type OutboxItem } from './db'
import { isNetworkError } from './refdata'

// The sync engine (App Flow §4.8, TRD §8.2–8.3, ADR-0005). It is the only code that sends queued entries.
//   PENDING ─▶ SYNCING ─▶ (CREATED | DUPLICATE: removed) | (REJECTED: NEEDS_ATTENTION) | (RETRY / network: PENDING with backoff)
// Only entries bound to the CURRENT user are sent. The server re-checks every rule; a rejected entry is never retried by itself.

const BATCH = 50
const BACKOFF_BASE_MS = 30_000
const BACKOFF_CAP_MS = 15 * 60_000

/** Wait before the next automatic attempt: 30 s, 1 min, 2 min … capped at 15 min (TRD §8.2). */
export const backoffMs = (attempts: number): number => Math.min(BACKOFF_CAP_MS, BACKOFF_BASE_MS * 2 ** Math.max(0, attempts - 1))

export type SyncReport = {
  sent: number
  synced: number
  rejected: number
  retrying: number
  skipped?: 'offline' | 'busy' | 'signed-out' | 'nothing'
}

type SyncStatus = { syncing: boolean; lastReport: SyncReport | null; reportId: number }

/** What the UI shows and reacts to: whether a run is in progress and the result of the latest one. */
export const useSyncStatus = create<SyncStatus>(() => ({ syncing: false, lastReport: null, reportId: 0 }))

let inflight: Promise<SyncReport> | null = null

/**
 * Sends what is due. <paramref name="force"/> (the "Sync now" button) ignores the backoff wait. Safe to call from
 * anywhere and as often as you like: runs never overlap, within a tab or across tabs.
 */
export function syncNow(options: { force?: boolean } = {}): Promise<SyncReport> {
  inflight ??= guarded(options.force === true).finally(() => {
    inflight = null
  })
  return inflight
}

async function guarded(force: boolean): Promise<SyncReport> {
  const locks = (navigator as Navigator & { locks?: LockManager }).locks
  if (!locks) return publish(await run(force))
  const result = await locks.request('fundledger-sync', { ifAvailable: true }, async (lock) => (lock ? run(force) : ({ sent: 0, synced: 0, rejected: 0, retrying: 0, skipped: 'busy' } as SyncReport)))
  return publish(result)
}

function publish(report: SyncReport): SyncReport {
  useSyncStatus.setState((s) => ({ syncing: false, lastReport: report, reportId: s.reportId + 1 }))
  return report
}

const empty = (skipped?: SyncReport['skipped']): SyncReport => ({ sent: 0, synced: 0, rejected: 0, retrying: 0, ...(skipped ? { skipped } : {}) })

async function run(force: boolean): Promise<SyncReport> {
  if (!navigator.onLine) return empty('offline')

  // An expired access token must not block entry (TRD §6.3); it is renewed here, when the queue is actually sent.
  let { user, accessToken } = useSession.getState()
  if (!user) return empty('signed-out')
  if (!accessToken) {
    await refresh()
    ;({ user, accessToken } = useSession.getState())
    if (!user) return empty('signed-out')
    if (!accessToken) return empty('offline')
  }

  useSyncStatus.setState({ syncing: true })
  const userId = user.id
  await sendDiscardNotices(userId)

  // A run that was cut short (tab closed) left entries in SYNCING; nothing else is running now, so they are simply due again.
  await offlineDb.outbox.where('userId').equals(userId).and((i) => i.state === 'SYNCING').modify({ state: 'PENDING' })

  const now = Date.now()
  const due = (await offlineDb.outbox.where('userId').equals(userId).sortBy('createdAt')).filter((i) => i.state === 'PENDING' && (force || i.nextAttemptAt <= now))
  if (due.length === 0) return empty('nothing')

  const report = empty()
  for (let start = 0; start < due.length; start += BATCH) {
    const chunk = due.slice(start, start + BATCH)
    const outcome = await sendChunk(chunk)
    report.sent += chunk.length
    report.synced += outcome.synced
    report.rejected += outcome.rejected
    report.retrying += outcome.retrying
    if (outcome.stop) break
  }

  return report
}

type Chunk = { synced: number; rejected: number; retrying: number; stop: boolean }

async function sendChunk(chunk: OutboxItem[]): Promise<Chunk> {
  const ids = chunk.map((i) => i.clientTxnId)
  await offlineDb.outbox.where('clientTxnId').anyOf(ids).modify({ state: 'SYNCING' })

  const backOff = async (message: string, count = true) => {
    const t = Date.now()
    await Promise.all(chunk.map((i) => offlineDb.outbox.update(i.clientTxnId, {
      state: 'PENDING', attempts: i.attempts + (count ? 1 : 0), nextAttemptAt: t + backoffMs(i.attempts + 1), lastError: { code: 'TRANSIENT', message },
    })))
    return { synced: 0, rejected: 0, retrying: chunk.length, stop: true }
  }

  let result
  try {
    result = await api.POST('/api/v1/sync/transactions', { body: { items: chunk.map((i) => i.command) } })
  } catch (error) {
    if (isNetworkError(error)) return backOff("Can't reach the server.")
    throw error
  }

  if (!result.response.ok || !result.data) {
    const status = result.response.status
    const title = (result.error as { title?: string } | undefined)?.title ?? 'The server refused this request.'
    if (status === 401) {
      // The session ended: keep everything queued for when the user signs in again.
      await offlineDb.outbox.where('clientTxnId').anyOf(ids).modify({ state: 'PENDING' })
      return { synced: 0, rejected: 0, retrying: chunk.length, stop: true }
    }

    if (status === 429 || status >= 500) return backOff(title)
    // 400/403/413...: the batch itself was refused. Someone has to look at it; retrying the same bytes won't help.
    await Promise.all(chunk.map((i) => offlineDb.outbox.update(i.clientTxnId, { state: 'NEEDS_ATTENTION', lastError: { code: 'BATCH_REFUSED', message: title } })))
    return { synced: 0, rejected: chunk.length, retrying: 0, stop: false }
  }

  let synced = 0
  let rejected = 0
  let retrying = 0
  const byId = new Map(chunk.map((i) => [i.clientTxnId, i]))
  for (const item of result.data.items) {
    const queued = byId.get(item.clientTxnId)
    if (!queued) continue
    byId.delete(item.clientTxnId)
    if (item.result === 'CREATED' || item.result === 'DUPLICATE') {
      await offlineDb.outbox.delete(queued.clientTxnId)
      synced++
    } else if (item.result === 'REJECTED') {
      await offlineDb.outbox.update(queued.clientTxnId, {
        state: 'NEEDS_ATTENTION', lastError: { code: item.errorCode ?? 'REJECTED', message: item.message ?? "This entry can't be added.", fieldErrors: item.fieldErrors ?? null },
      })
      rejected++
    } else {
      await offlineDb.outbox.update(queued.clientTxnId, {
        state: 'PENDING', attempts: queued.attempts + 1, nextAttemptAt: Date.now() + backoffMs(queued.attempts + 1), lastError: { code: 'TRANSIENT', message: item.message ?? 'Will try again.' },
      })
      retrying++
    }
  }

  // Anything the server did not answer for is simply due again next time.
  for (const left of byId.values()) await offlineDb.outbox.update(left.clientTxnId, { state: 'PENDING' })
  return { synced, rejected, retrying, stop: false }
}

/** Tells the audit log about entries the user discarded while offline (best effort; kept until it succeeds). */
async function sendDiscardNotices(userId: string): Promise<void> {
  const notices = await offlineDb.discards.where('userId').equals(userId).toArray()
  for (const n of notices) {
    try {
      const r = await api.POST('/api/v1/sync/discard', { body: { clientTxnId: n.clientTxnId, summary: n.summary } })
      if (r.response.ok) await offlineDb.discards.delete(n.clientTxnId)
    } catch (error) {
      if (isNetworkError(error) || error instanceof ApiError) return
      throw error
    }
  }
}
