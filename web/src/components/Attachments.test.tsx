/// <reference types="node" />
import { File as NodeFile } from 'node:buffer'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { routes } from '../app/router'
import { useSession } from '../lib/auth/session'
import { offlineDb, type SyncCommand } from '../lib/offline/db'
import { enqueue, listFor } from '../lib/offline/outbox'
import { syncNow } from '../lib/offline/sync'
import { ApiError } from '../lib/api/errors'
import { uploadAttachment } from '../lib/api/attachments'
import { adminUser, fakeApi, json, memberUser, meFor } from '../test/fakeApi'

// The multipart upload itself is exercised end to end by the API tests. Here the browser's FormData (jsdom's) can't be read
// back the way a real one can, so the upload call is replaced and these tests check which files go to which entry, and when.
vi.mock('../lib/api/attachments', async (original) => ({ ...(await original<typeof import('../lib/api/attachments')>()), uploadAttachment: vi.fn() }))
const upload = vi.mocked(uploadAttachment)

const accounts = [{ id: 'a-cash', name: 'Main Cash', kind: 'CASH', bankName: null, accountNumberLast4: null, isActive: true, sortOrder: 0 }]
const modes = [{ id: 'm-cash', name: 'Cash', requiresReference: false, isActive: true, sortOrder: 0 }]
const cats = [{ id: 'c-coll', fundId: null, direction: 'MONEY_IN', name: 'Collection', icon: null, isActive: true, sortOrder: 0 }]
const attachment = (over: Record<string, unknown> = {}) => ({
  id: 'f1', fileName: 'bill.pdf', contentType: 'application/pdf', sizeBytes: 20480, uploadedBy: { id: 'u-admin', name: 'Asha Admin' }, uploadedAt: '2026-10-13T05:00:00Z',
  isHidden: false, hiddenReason: null, ...over,
})
const pdf = (name = 'bill.pdf') => new NodeFile(['%PDF-1.4'], name, { type: 'application/pdf' }) as unknown as File
const setOnline = (value: boolean) => Object.defineProperty(navigator, 'onLine', { value, configurable: true })

const created = {
  transaction: {
    id: 't1', txnNumber: 'IJT26-2026-27-000001', type: 'DEPOSIT', status: 'ACTIVE', amount: '2500.00', fundId: 'f1', txnDate: '2026-10-13', txnTime: '10:00',
    createdBy: { id: 'u-admin', name: 'Asha Admin' }, createdAt: '2026-10-13T04:30:00Z', revision: 1, source: 'ONLINE',
  },
  fundClosingBalance: '2500.00', duplicate: false,
}

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

describe('attaching files', () => {
  let api: ReturnType<typeof fakeApi> | undefined
  beforeEach(async () => {
    upload.mockReset()
    upload.mockResolvedValue(attachment() as never)
    localStorage.clear()
    setOnline(true)
    URL.createObjectURL = vi.fn(() => 'blob:x')
    URL.revokeObjectURL = vi.fn()
    useSession.setState({ status: 'authenticated', accessToken: 't', user: adminUser })
    await Promise.all([offlineDb.outbox.clear(), offlineDb.refdata.clear(), offlineDb.meta.clear(), offlineDb.discards.clear()])
  })
  afterEach(() => {
    api?.restore()
    setOnline(true)
    useSession.setState({ status: 'unknown', accessToken: null, user: null })
  })

  it('uploads the chosen files once the entry is recorded', async () => {
    api = fakeApi({ ...reference, 'POST /api/v1/transactions/deposit': () => json(201, created) })
    renderAt('/new/in')
    await userEvent.type(await screen.findByLabelText(/^Amount/), '2500')
    await screen.findByRole('radio', { name: 'Collection' })
    await userEvent.upload(screen.getByLabelText('Choose file', { selector: 'input' }), pdf('bill.pdf'))
    expect(await screen.findByText('bill.pdf')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Review & save' }))
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Save Money In' }))
    expect(await screen.findByText('1 file(s) attached.')).toBeInTheDocument()
    expect(upload).toHaveBeenCalledTimes(1)
    expect(upload.mock.calls[0]![0]).toBe('t1')
    expect(upload.mock.calls[0]![2]).toBe('bill.pdf')
  })

  it('keeps the entry and says so when a file is refused', async () => {
    api = fakeApi({ ...reference, 'POST /api/v1/transactions/deposit': () => json(201, created) })
    upload.mockRejectedValue(new ApiError(415, 'ATTACHMENT_TYPE_NOT_ALLOWED', 'Only photos and PDF files can be attached.'))
    renderAt('/new/in')
    await userEvent.type(await screen.findByLabelText(/^Amount/), '2500')
    await screen.findByRole('radio', { name: 'Collection' })
    await userEvent.upload(screen.getByLabelText('Choose file', { selector: 'input' }), pdf())
    await userEvent.click(screen.getByRole('button', { name: 'Review & save' }))
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Save Money In' }))
    expect(await screen.findByText('IJT26-2026-27-000001')).toBeInTheDocument()
    expect(screen.getByText(/couldn't be added/)).toBeInTheDocument()
  })

  it('keeps files on the device with an offline entry, then uploads them after the entry syncs (TR-055)', async () => {
    api = fakeApi({
      'POST /api/v1/sync/transactions': async (req) => {
        const { items } = (await req.json()) as { items: { clientTxnId: string }[] }
        return json(200, { items: items.map((i) => ({ clientTxnId: i.clientTxnId, result: 'CREATED', transaction: created.transaction, errorCode: null, message: null, fieldErrors: null })) })
      },
    })
    const command: SyncCommand = {
      clientTxnId: 'q1', type: 'DEPOSIT', clientCreatedAt: '2026-10-13T05:00:00Z', fundId: 'f1', amount: '2500.00', txnDate: '2026-10-13', txnTime: '10:00', categoryId: 'c-coll',
      accountId: 'a-cash', fromAccountId: null, toAccountId: null, paymentModeId: 'm-cash', receivedFrom: null, paidTo: null, purpose: 'Collection', referenceNumber: null, remarks: null,
    }
    await enqueue({ userId: adminUser.id, orgId: 'o1' }, command, { fundName: 'Ijtema 2026' }, [pdf('one.pdf'), pdf('two.pdf')])
    expect(await syncNow()).toMatchObject({ synced: 1 })
    // (The fake IndexedDB drops a File's name on the way through; real browsers keep it. Count and target are what matter here.)
    expect(upload).toHaveBeenCalledTimes(2)
    expect(upload.mock.calls.every((c) => c[0] === 't1' && (c[1] as Blob).size === 8)).toBe(true)
    expect(await listFor(adminUser.id)).toHaveLength(0)
  })

  it('waits for a better connection when an upload fails in transit, and gives up on a file the server will never take', async () => {
    api = fakeApi({})
    upload.mockRejectedValue(ApiError.network())
    await enqueue({ userId: adminUser.id, orgId: 'o1' }, { clientTxnId: 'q2' } as SyncCommand, { fundName: 'Ijtema 2026' }, [pdf('a.pdf'), pdf('b.pdf')])
    await offlineDb.outbox.update('q2', { state: 'FILES', serverTxnId: 't1' })

    await syncNow({ force: true })
    const [waiting] = await listFor(adminUser.id)
    expect(waiting).toMatchObject({ state: 'FILES', attempts: 1 })
    expect(waiting!.attachments).toHaveLength(2)                                   // nothing lost

    upload.mockRejectedValue(new ApiError(415, 'ATTACHMENT_TYPE_NOT_ALLOWED', 'Not allowed.'))
    await syncNow({ force: true })
    expect(await listFor(adminUser.id)).toHaveLength(0)                            // both refused for good: nothing left to retry
  })

  it('shows the files on an entry, lets the owner add one, and lets only an Admin hide one', async () => {
    const detail = (createdBy: string) => ({
      transaction: { ...created.transaction, createdBy: { id: createdBy, name: 'Someone' }, category: null, account: null, fromAccount: null, toAccount: null, paymentMode: null,
        adjustmentDirection: null, receivedFrom: null, paidTo: null, purpose: 'Collection', referenceNumber: null, remarks: null, updatedBy: null, updatedAt: null, cancelledBy: null, cancelledAt: null, cancellationReason: null },
      canEdit: false, canCancel: false, editableUntil: null, editRequiresReason: false,
    })
    let hidden: unknown
    api = fakeApi({
      ...reference,
      'GET /api/v1/transactions/t1': () => json(200, detail('u-admin')),
      'GET /api/v1/transactions/t1/history': () => json(200, []),
      'GET /api/v1/transactions/t1/attachments': () => json(200, [attachment(), attachment({ id: 'f2', fileName: 'old.pdf', isHidden: true, hiddenReason: 'Private' })]),
      'POST /api/v1/attachments/f1/hide': async (req) => { hidden = await req.json(); return json(200, attachment({ isHidden: true })) },
    })
    renderAt('/txn/t1')
    expect(await screen.findByText('bill.pdf')).toBeInTheDocument()
    expect(screen.getByText('old.pdf')).toBeInTheDocument()                        // the Admin sees hidden files, marked
    expect(screen.getByLabelText('Add file', { selector: 'input' })).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Hide' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByRole('button', { name: 'Hide' })).toBeDisabled()    // a reason is required
    await userEvent.type(within(dialog).getByLabelText('Reason'), 'Shows bank details')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Hide' }))
    await waitFor(() => expect(hidden).toEqual({ reason: 'Shows bank details' }))
  })

  it('shows a Member no hide button, and no add button on someone else\'s entry', async () => {
    useSession.setState({ status: 'authenticated', accessToken: 't', user: memberUser })
    api = fakeApi({
      'GET /api/v1/me': () => json(200, meFor(memberUser)),
      'GET /api/v1/transactions/t1': () => json(200, {
        transaction: { ...created.transaction, createdBy: { id: 'u-admin', name: 'Asha Admin' }, category: null, account: null, fromAccount: null, toAccount: null, paymentMode: null, adjustmentDirection: null,
          receivedFrom: null, paidTo: null, purpose: 'Collection', referenceNumber: null, remarks: null, updatedBy: null, updatedAt: null, cancelledBy: null, cancelledAt: null, cancellationReason: null },
        canEdit: false, canCancel: false, editableUntil: null, editRequiresReason: false,
      }),
      'GET /api/v1/transactions/t1/history': () => json(200, []),
      'GET /api/v1/transactions/t1/attachments': () => json(200, [attachment()]),
    })
    renderAt('/txn/t1')
    expect(await screen.findByText('bill.pdf')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Hide' })).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Add file', { selector: 'input' })).not.toBeInTheDocument()
  })
})
