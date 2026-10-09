import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useSession } from '../auth/session'
import { adminUser, fakeApi, json, memberUser } from '../../test/fakeApi'
import { offlineDb, type OutboxItem, type SyncCommand } from './db'
import { discard, enqueue, listFor } from './outbox'
import { loadLastSession, saveLastSession } from './refdata'
import { backoffMs, syncNow } from './sync'

const owner: { userId: string; orgId: string } = { userId: adminUser.id, orgId: 'o1' }

function command(id: string, over: Partial<SyncCommand> = {}): SyncCommand {
  return {
    clientTxnId: id, type: 'DEPOSIT', clientCreatedAt: '2026-10-09T05:00:00Z', fundId: 'f1', amount: '100.00', txnDate: '2026-10-09', txnTime: '10:00',
    categoryId: 'c1', accountId: 'a1', fromAccountId: null, toAccountId: null, paymentModeId: 'm1', receivedFrom: 'Area 4', paidTo: null, purpose: 'Collection',
    referenceNumber: null, remarks: null, ...over,
  }
}

const queue = (id: string, who = owner, over: Partial<SyncCommand> = {}) => enqueue(who, command(id, over), { fundName: 'Ijtema 2026', category: 'Collection', account: 'Main Cash' })
const reply = (results: { id: string; result: string; errorCode?: string; message?: string }[]) =>
  json(200, { items: results.map((r) => ({ clientTxnId: r.id, result: r.result, transaction: null, errorCode: r.errorCode ?? null, message: r.message ?? null, fieldErrors: null })) })
const setOnline = (value: boolean) => Object.defineProperty(navigator, 'onLine', { value, configurable: true })

describe('backoff', () => {
  it('doubles from 30 seconds and stops at 15 minutes (TRD §8.2)', () => {
    expect([1, 2, 3, 4, 5, 6, 7, 10].map(backoffMs)).toEqual([30_000, 60_000, 120_000, 240_000, 480_000, 900_000, 900_000, 900_000])
  })
})

describe('sync engine', () => {
  let api: ReturnType<typeof fakeApi> | undefined
  beforeEach(async () => {
    setOnline(true)
    useSession.setState({ status: 'authenticated', accessToken: 't', user: adminUser })
    await Promise.all([offlineDb.outbox.clear(), offlineDb.discards.clear(), offlineDb.meta.clear(), offlineDb.refdata.clear()])
  })
  afterEach(() => {
    api?.restore()
    vi.useRealTimers()
    useSession.setState({ status: 'unknown', accessToken: null, user: null })
  })

  const posts = () => api!.calls.filter((c) => c.method === 'POST' && c.url.endsWith('/sync/transactions'))
  const sentIds = async () => (await Promise.all(posts().map(async (c) => ((await c.clone().json()) as { items: { clientTxnId: string }[] }).items.map((i) => i.clientTxnId)))).flat()

  it('removes entries the server recorded, including ones it already had', async () => {
    api = fakeApi({ 'POST /api/v1/sync/transactions': () => reply([{ id: 'a', result: 'CREATED' }, { id: 'b', result: 'DUPLICATE' }]) })
    await queue('a')
    await queue('b')
    const report = await syncNow()
    expect(report).toMatchObject({ sent: 2, synced: 2, rejected: 0, retrying: 0 })
    expect(await listFor(adminUser.id)).toHaveLength(0)
  })

  it('keeps a rejected entry for the user, shows the reason, and never sends it again by itself', async () => {
    api = fakeApi({ 'POST /api/v1/sync/transactions': () => reply([{ id: 'a', result: 'REJECTED', errorCode: 'FUND_NOT_ACTIVE', message: 'Ijtema 2026 is closed.' }]) })
    await queue('a')
    expect(await syncNow()).toMatchObject({ rejected: 1 })
    const [item] = await listFor(adminUser.id)
    expect(item).toMatchObject({ state: 'NEEDS_ATTENTION', lastError: { code: 'FUND_NOT_ACTIVE', message: 'Ijtema 2026 is closed.' } })

    expect(await syncNow({ force: true })).toMatchObject({ skipped: 'nothing' })          // even "Sync now" leaves it alone
    expect(posts()).toHaveLength(1)
  })

  it('backs off when the network fails and sends again only when due, or when forced', async () => {
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(new Date('2026-10-09T10:00:00Z'))
    let failing = true
    api = fakeApi({ 'POST /api/v1/sync/transactions': () => { if (failing) throw new TypeError('Failed to fetch'); return reply([{ id: 'a', result: 'CREATED' }]) } })
    await queue('a')

    expect(await syncNow()).toMatchObject({ retrying: 1 })
    const [waiting] = await listFor(adminUser.id)
    expect(waiting).toMatchObject({ state: 'PENDING', attempts: 1, nextAttemptAt: Date.now() + 30_000 })

    failing = false
    vi.setSystemTime(new Date('2026-10-09T10:00:10Z'))
    expect(await syncNow()).toMatchObject({ skipped: 'nothing' })                         // not due yet
    expect(posts()).toHaveLength(1)

    vi.setSystemTime(new Date('2026-10-09T10:00:31Z'))
    expect(await syncNow()).toMatchObject({ synced: 1 })                                  // due
    expect(await listFor(adminUser.id)).toHaveLength(0)
  })

  it('"Sync now" ignores the wait', async () => {
    api = fakeApi({ 'POST /api/v1/sync/transactions': () => reply([{ id: 'a', result: 'CREATED' }]) })
    await queue('a')
    await offlineDb.outbox.update('a', { attempts: 3, nextAttemptAt: Date.now() + 600_000 })
    expect(await syncNow()).toMatchObject({ skipped: 'nothing' })
    expect(await syncNow({ force: true })).toMatchObject({ synced: 1 })
  })

  it('treats a transient answer from the server as retry, and a server error as backoff', async () => {
    api = fakeApi({ 'POST /api/v1/sync/transactions': () => reply([{ id: 'a', result: 'RETRY', message: 'Busy.' }]) })
    await queue('a')
    expect(await syncNow()).toMatchObject({ retrying: 1 })
    expect((await listFor(adminUser.id))[0]).toMatchObject({ state: 'PENDING', attempts: 1 })
    api.restore()

    api = fakeApi({ 'POST /api/v1/sync/transactions': () => json(503, { title: 'Down.' }) })
    await syncNow({ force: true })
    expect((await listFor(adminUser.id))[0]).toMatchObject({ state: 'PENDING', attempts: 2 })
  })

  it('never sends another user\'s entries (TRD §6.3), and keeps them for that user', async () => {
    api = fakeApi({ 'POST /api/v1/sync/transactions': () => reply([{ id: 'mine', result: 'CREATED' }]) })
    await queue('mine')
    await queue('theirs', { userId: memberUser.id, orgId: 'o1' })
    await syncNow()
    expect(await sentIds()).toEqual(['mine'])
    expect(await listFor(memberUser.id)).toHaveLength(1)
  })

  it('sends at most 50 per request, oldest first', async () => {
    const all = Array.from({ length: 55 }, (_, i) => `id-${String(i).padStart(2, '0')}`)
    api = fakeApi({ 'POST /api/v1/sync/transactions': async (req) => reply(((await req.json()) as { items: { clientTxnId: string }[] }).items.map((i) => ({ id: i.clientTxnId, result: 'CREATED' }))) })
    for (const [i, id] of all.entries()) await queue(id, owner, { clientCreatedAt: `2026-10-09T05:${String(i).padStart(2, '0')}:00Z` })
    expect(await syncNow()).toMatchObject({ sent: 55, synced: 55 })
    const sizes = await Promise.all(posts().map(async (c) => ((await c.clone().json()) as { items: unknown[] }).items.length))
    expect(sizes).toEqual([50, 5])
    expect((await sentIds())[0]).toBe('id-00')
  })

  it('does nothing while offline, and keeps the entries', async () => {
    api = fakeApi({})
    await queue('a')
    setOnline(false)
    expect(await syncNow()).toMatchObject({ skipped: 'offline' })
    expect(api.calls).toHaveLength(0)
    expect(await listFor(adminUser.id)).toHaveLength(1)
  })

  it('keeps everything queued when the session has ended (401), without counting an attempt', async () => {
    api = fakeApi({ 'POST /api/v1/sync/transactions': () => json(401, { code: 'UNAUTHENTICATED', title: 'Please sign in again.' }) })
    await queue('a')
    await syncNow()
    expect((await listFor(adminUser.id))[0]).toMatchObject({ state: 'PENDING', attempts: 0 })
  })

  it('tells the audit log about discarded entries once it can, and forgets the notice after', async () => {
    api = fakeApi({ 'POST /api/v1/sync/discard': () => new Response(null, { status: 204 }) })
    const item = (await queue('a')) as OutboxItem
    await discard(item, 'Money In ₹100 · Collection')
    expect(await listFor(adminUser.id)).toHaveLength(0)
    expect(await offlineDb.discards.count()).toBe(1)

    await syncNow()
    expect(await offlineDb.discards.count()).toBe(0)
    const sent = (await api.calls.find((c) => c.url.endsWith('/sync/discard'))!.json()) as { clientTxnId: string }
    expect(sent.clientTxnId).toBe('a')
  })
})

describe('opening the app with no connection', () => {
  beforeEach(async () => {
    await offlineDb.meta.clear()
    useSession.setState({ status: 'unknown', accessToken: null, user: null })
  })
  afterEach(() => vi.restoreAllMocks())

  it('restores the last signed-in profile without a token, so entering can continue (TRD §6.3)', async () => {
    const { refresh } = await import('../auth/session')
    await saveLastSession(adminUser)
    vi.spyOn(globalThis, 'fetch').mockRejectedValue(new TypeError('Failed to fetch'))
    expect(await refresh()).toBe(true)
    expect(useSession.getState()).toMatchObject({ status: 'authenticated', accessToken: null, user: { id: adminUser.id } })
  })

  it('shows the sign-in page when nobody has signed in on this device', async () => {
    const { refresh } = await import('../auth/session')
    vi.spyOn(globalThis, 'fetch').mockRejectedValue(new TypeError('Failed to fetch'))
    expect(await refresh()).toBe(false)
    expect(useSession.getState().status).toBe('anonymous')
    expect(await loadLastSession()).toBeUndefined()
  })
})
