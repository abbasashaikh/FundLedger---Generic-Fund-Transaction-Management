import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { routes } from '../../app/router'
import { useSession } from '../../lib/auth/session'
import { offlineDb, type SyncCommand } from '../../lib/offline/db'
import { enqueue, listFor } from '../../lib/offline/outbox'
import { syncNow } from '../../lib/offline/sync'
import { adminUser, fakeApi, json, meFor } from '../../test/fakeApi'

const accounts = [{ id: 'a-cash', name: 'Main Cash', kind: 'CASH', bankName: null, accountNumberLast4: null, isActive: true, sortOrder: 0 }]
const modes = [{ id: 'm-cash', name: 'Cash', requiresReference: false, isActive: true, sortOrder: 0 }]
const cats = [{ id: 'c-coll', fundId: null, direction: 'MONEY_IN', name: 'Collection', icon: null, isActive: true, sortOrder: 0 }]
const setOnline = (value: boolean) => Object.defineProperty(navigator, 'onLine', { value, configurable: true })

const reference = {
  'GET /api/v1/me': () => json(200, meFor(adminUser)),
  'GET /api/v1/accounts': () => json(200, accounts),
  'GET /api/v1/payment-modes': () => json(200, modes),
  'GET /api/v1/categories': () => json(200, cats),
  'GET /api/v1/accounts/balances': () => json(200, []),
}

function renderAt(path: string) {
  const router = createMemoryRouter(routes, { initialEntries: [path] })
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  )
  return router
}

const queued = (id: string, over: Partial<SyncCommand> = {}): SyncCommand => ({
  clientTxnId: id, type: 'DEPOSIT', clientCreatedAt: '2026-10-09T05:00:00Z', fundId: 'f1', amount: '2500.00', txnDate: '2026-10-09', txnTime: '10:00',
  categoryId: 'c-coll', accountId: 'a-cash', fromAccountId: null, toAccountId: null, paymentModeId: 'm-cash', receivedFrom: 'Area 4', paidTo: null,
  purpose: 'Collection', referenceNumber: null, remarks: null, ...over,
})

describe('offline entry and the sync screen', () => {
  let api: ReturnType<typeof fakeApi> | undefined
  beforeEach(async () => {
    localStorage.clear()
    setOnline(true)
    useSession.setState({ status: 'authenticated', accessToken: 't', user: adminUser })
    await Promise.all([offlineDb.outbox.clear(), offlineDb.discards.clear(), offlineDb.refdata.clear(), offlineDb.meta.clear(), offlineDb.ledgerCache.clear()])
  })
  afterEach(() => {
    api?.restore()
    setOnline(true)
    useSession.setState({ status: 'unknown', accessToken: null, user: null })
  })

  async function fillAndSave() {
    await userEvent.type(await screen.findByLabelText(/^Amount/), '2500')
    await screen.findByRole('radio', { name: 'Collection' })
    await userEvent.click(screen.getByRole('button', { name: 'Review & save' }))
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Save Money In' }))
  }

  it('saves on the device when the request cannot get through, then sends the SAME client id when the connection returns', async () => {
    let down = true
    const seen: string[] = []
    api = fakeApi({
      ...reference,
      'POST /api/v1/transactions/deposit': async (req) => { seen.push(((await req.json()) as { clientTxnId: string }).clientTxnId); throw new TypeError('Failed to fetch') },
      'POST /api/v1/sync/transactions': async (req) => {
        if (down) throw new TypeError('Failed to fetch')
        const { items } = (await req.json()) as { items: { clientTxnId: string }[] }
        return json(200, { items: items.map((i) => ({ clientTxnId: i.clientTxnId, result: 'CREATED', transaction: null, errorCode: null, message: null, fieldErrors: null })) })
      },
    })
    renderAt('/new/in')
    await fillAndSave()

    expect(await screen.findByRole('heading', { name: 'Saved on this device' })).toBeInTheDocument()
    const [item] = await listFor(adminUser.id)
    expect(item).toMatchObject({ state: 'PENDING', command: { type: 'DEPOSIT', amount: '2500.00', fundId: 'f1' }, display: { category: 'Collection', account: 'Main Cash' } })
    expect(item!.clientTxnId).toBe(seen[0])                                  // what was attempted online is what is queued

    down = false
    expect(await syncNow({ force: true })).toMatchObject({ synced: 1 })
    expect(await listFor(adminUser.id)).toHaveLength(0)
  })

  it('goes straight to the queue when the device is offline, using reference data remembered from the last visit', async () => {
    api = fakeApi(reference)
    const first = renderAt('/new/in')
    await screen.findByRole('radio', { name: 'Collection' })
    await first.navigate('/')
    api.restore()
    document.body.innerHTML = ''

    setOnline(false)
    api = fakeApi({})                                                       // every request now fails
    api = undefined
    const calls: string[] = []
    const real = globalThis.fetch
    globalThis.fetch = (async (input: RequestInfo | URL) => { calls.push(String(input)); throw new TypeError('Failed to fetch') }) as typeof fetch
    try {
      renderAt('/new/in')
      await userEvent.type(await screen.findByLabelText(/^Amount/), '700')
      expect(await screen.findByRole('radio', { name: 'Collection' })).toBeChecked()      // from this device, not the network
      await userEvent.click(screen.getByRole('button', { name: 'Review & save' }))
      await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Save Money In' }))
      expect(await screen.findByRole('heading', { name: 'Saved on this device' })).toBeInTheDocument()
      expect(calls.some((c) => c.endsWith('/transactions/deposit'))).toBe(false)           // offline: no doomed request
      expect((await listFor(adminUser.id))[0]).toMatchObject({ command: { amount: '700.00' } })
    } finally {
      globalThis.fetch = real
    }
  })

  it('shows what is waiting and what needs attention, with the reason in plain words', async () => {
    api = fakeApi(reference)
    const owner = { userId: adminUser.id, orgId: 'o1' }
    await enqueue(owner, queued('wait'), { fundName: 'Ijtema 2026', category: 'Collection', account: 'Main Cash' })
    await enqueue(owner, queued('bad', { amount: '999.00' }), { fundName: 'Ijtema 2026', category: 'Collection', account: 'Main Cash' })
    await offlineDb.outbox.update('bad', { state: 'NEEDS_ATTENTION', lastError: { code: 'FUND_NOT_ACTIVE', message: 'Ijtema 2026 was closed. This entry can’t be added.' } })

    renderAt('/sync')
    expect(await screen.findByText('Needs your attention')).toBeInTheDocument()
    expect(screen.getByText(/was closed\. This entry can’t be added\./)).toBeInTheDocument()
    expect(screen.getByText('Waiting to be sent')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Edit & retry' })).toBeInTheDocument()
  })

  it('discards a rejected entry only after confirmation, and records the discard', async () => {
    api = fakeApi({ ...reference, 'POST /api/v1/sync/discard': () => new Response(null, { status: 204 }) })
    await enqueue({ userId: adminUser.id, orgId: 'o1' }, queued('bad'), { fundName: 'Ijtema 2026', category: 'Collection', account: 'Main Cash' })
    await offlineDb.outbox.update('bad', { state: 'NEEDS_ATTENTION', lastError: { code: 'FUND_NOT_ACTIVE', message: 'Closed.' } })

    renderAt('/sync')
    await userEvent.click(await screen.findByRole('button', { name: 'Discard' }))
    const dialog = await screen.findByRole('dialog')
    expect(await listFor(adminUser.id)).toHaveLength(1)                       // nothing happens before the confirmation
    await userEvent.click(within(dialog).getByRole('button', { name: 'Discard' }))
    await expect.poll(async () => (await listFor(adminUser.id)).length).toBe(0)
    await expect.poll(() => api!.calls.some((c) => c.url.endsWith('/sync/discard'))).toBe(true)
  })

  it('"Edit & retry" reopens the entry with its values and sends the fixed entry again under the same id', async () => {
    api = fakeApi({ ...reference, 'POST /api/v1/sync/transactions': () => json(200, { items: [] }) })
    await enqueue({ userId: adminUser.id, orgId: 'o1' }, queued('bad', { amount: '999.00', purpose: 'Wrong purpose' }), { fundName: 'Ijtema 2026', category: 'Collection', account: 'Main Cash' })
    await offlineDb.outbox.update('bad', { state: 'NEEDS_ATTENTION', attempts: 2, lastError: { code: 'CATEGORY_INACTIVE', message: 'Collection was turned off.' } })

    renderAt('/new/in?resume=bad')
    const amount = await screen.findByLabelText(/^Amount/)
    expect(amount).toHaveValue('999.00')
    const purpose = screen.getByLabelText(/^Purpose/)
    expect(purpose).toHaveValue('Wrong purpose')
    await userEvent.clear(purpose)
    await userEvent.type(purpose, 'Corrected purpose')
    await userEvent.click(screen.getByRole('button', { name: 'Review & save' }))
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Save Money In' }))
    expect(await screen.findByRole('heading', { name: 'Saved on this device' })).toBeInTheDocument()
    const [item] = await listFor(adminUser.id)
    expect(item).toMatchObject({ clientTxnId: 'bad', command: { purpose: 'Corrected purpose' } })
  })

  it('shows the top-bar status and disables edit and cancel with a reason while offline', async () => {
    const detail = {
      transaction: {
        id: 't1', txnNumber: 'IJT26-2026-27-000001', type: 'EXPENSE', status: 'ACTIVE', amount: '100.00', fundId: 'f1', txnDate: '2026-10-09', txnTime: '10:00',
        category: null, account: null, fromAccount: null, toAccount: null, paymentMode: null, adjustmentDirection: null, receivedFrom: null, paidTo: 'Caterer', purpose: 'Lunch',
        referenceNumber: null, remarks: null, createdBy: { id: 'u', name: 'Asha' }, createdAt: '2026-10-09T04:30:00Z', revision: 1, source: 'ONLINE',
        updatedBy: null, updatedAt: null, cancelledBy: null, cancelledAt: null, cancellationReason: null,
      },
      canEdit: true, canCancel: true, editableUntil: null, editRequiresReason: true,
    }
    api = fakeApi({ ...reference, 'GET /api/v1/transactions/t1': () => json(200, detail), 'GET /api/v1/transactions/t1/history': () => json(200, []) })
    await enqueue({ userId: adminUser.id, orgId: 'o1' }, queued('wait'), { fundName: 'Ijtema 2026', category: 'Collection', account: 'Main Cash' })
    setOnline(false)
    renderAt('/txn/t1')
    const edit = await screen.findByRole('button', { name: 'Edit' })
    expect(edit).toBeDisabled()
    expect(edit).toHaveAttribute('title', 'Needs internet connection')
    expect(screen.getByRole('button', { name: 'Cancel entry' })).toBeDisabled()
    expect(await screen.findByRole('link', { name: /waiting to sync/ })).toHaveAttribute('href', '/sync')
    expect(screen.getByText(/You're offline/)).toBeInTheDocument()
  })
})
