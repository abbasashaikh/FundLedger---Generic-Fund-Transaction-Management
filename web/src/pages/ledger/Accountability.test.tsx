import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { routes } from '../../app/router'
import { useSession } from '../../lib/auth/session'
import { adminUser, fakeApi, json, memberUser, meFor } from '../../test/fakeApi'

const accounts = [
  { id: 'a-cash', name: 'Main Cash', kind: 'CASH', bankName: null, accountNumberLast4: null, isActive: true, sortOrder: 0 },
  { id: 'a-old', name: 'Old Box', kind: 'CASH', bankName: null, accountNumberLast4: null, isActive: false, sortOrder: 1 },
]
const modes = [{ id: 'm-cash', name: 'Cash', requiresReference: false, isActive: true, sortOrder: 0 }]
const cats = [{ id: 'c-coll', fundId: null, direction: 'MONEY_IN', name: 'Collection', icon: null, isActive: true, sortOrder: 0 }]

const txn = (over: Record<string, unknown> = {}) => ({
  id: 't1', txnNumber: 'IJT26-2026-27-000001', type: 'DEPOSIT', status: 'ACTIVE', amount: '100.00', fundId: 'f1',
  txnDate: '2026-10-08', txnTime: '10:00', category: { id: 'c-coll', name: 'Collection' }, account: { id: 'a-old', name: 'Old Box' },
  fromAccount: null, toAccount: null, paymentMode: { id: 'm-cash', name: 'Cash' }, adjustmentDirection: null,
  receivedFrom: 'Yusuf', paidTo: null, purpose: 'Collection', referenceNumber: null, remarks: null,
  createdBy: { id: 'u-admin', name: 'Asha Admin' }, createdAt: '2026-10-08T04:30:00Z', revision: 3, source: 'ONLINE',
  updatedBy: null, updatedAt: null, cancelledBy: null, cancelledAt: null, cancellationReason: null, ...over,
})
const detail = (over: Record<string, unknown> = {}, t: Record<string, unknown> = {}) =>
  ({ transaction: txn(t), canEdit: true, canCancel: true, editableUntil: null, editRequiresReason: true, ...over })
const history = [
  { revision: 2, kind: 'EDITED', changedBy: { id: 'u-admin', name: 'Asha Admin' }, changedAt: '2026-10-08T05:00:00Z', reason: 'Bill said 100',
    changes: [{ field: 'amount', old: '90.00', new: '100.00' }] },
  { revision: 1, kind: 'CREATED', changedBy: { id: 'u-admin', name: 'Asha Admin' }, changedAt: '2026-10-08T04:30:00Z', reason: null, changes: [] },
]

function renderAt(path: string) {
  const router = createMemoryRouter(routes, { initialEntries: [path] })
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  )
  return router
}

describe('Transaction detail, edit and cancel', () => {
  let api: ReturnType<typeof fakeApi> | undefined
  beforeEach(() => useSession.setState({ status: 'authenticated', accessToken: 't', user: adminUser }))
  afterEach(() => api?.restore())

  const base = (d: unknown, extra: Record<string, (req: Request) => Response | Promise<Response>> = {}) => fakeApi({
    'GET /api/v1/me': () => json(200, meFor(adminUser)),
    'GET /api/v1/transactions/t1': () => json(200, d),
    'GET /api/v1/transactions/t1/history': () => json(200, history),
    'GET /api/v1/accounts': () => json(200, accounts),
    'GET /api/v1/payment-modes': () => json(200, modes),
    'GET /api/v1/categories': () => json(200, cats),
    ...extra,
  })

  it('shows the history with old and new values', async () => {
    api = base(detail())
    renderAt('/txn/t1')
    const section = await screen.findByRole('region', { name: 'History' })
    expect(await within(section).findByText('Reason: Bill said 100')).toBeInTheDocument()
    expect(within(section).getByText('₹90.00')).toBeInTheDocument()
    expect(within(section).getByText('₹100.00')).toBeInTheDocument()
    expect(within(section).getByText('Recorded')).toBeInTheDocument()
  })

  it('hides edit and cancel when the caller may not use them', async () => {
    api = base(detail({ canEdit: false, canCancel: false }))
    renderAt('/txn/t1')
    await screen.findByRole('heading', { name: 'IJT26-2026-27-000001' })
    expect(screen.queryByRole('button', { name: /edit/i })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Cancel entry' })).not.toBeInTheDocument()
  })

  it('cancels with a reason and the revision it saw', async () => {
    let sent: { ifMatch: string | null; body: unknown } | undefined
    api = base(detail(), {
      'POST /api/v1/transactions/t1/cancel': async (req) => {
        sent = { ifMatch: req.headers.get('If-Match'), body: await req.json() }
        return json(200, { transaction: txn({ status: 'CANCELLED', revision: 4 }), fundClosingBalance: '0.00', duplicate: false })
      },
    })
    renderAt('/txn/t1')
    await userEvent.click(await screen.findByRole('button', { name: 'Cancel entry' }))
    const dialog = await screen.findByRole('dialog')
    const confirm = within(dialog).getByRole('button', { name: 'Cancel entry' })
    expect(confirm).toBeDisabled()                                      // reason is required
    await userEvent.type(within(dialog).getByLabelText(/Reason for cancelling/), 'Entered twice')
    await userEvent.click(confirm)
    await screen.findByText('Cancelled · IJT26-2026-27-000001')
    expect(sent).toEqual({ ifMatch: '"3"', body: { reason: 'Entered twice' } })
  })

  it('edits with If-Match, keeps a turned-off account, and an Admin must give a reason', async () => {
    let sent: { ifMatch: string | null; body: Record<string, unknown> } | undefined
    api = base(detail(), {
      'PUT /api/v1/transactions/t1': async (req) => {
        sent = { ifMatch: req.headers.get('If-Match'), body: (await req.json()) as Record<string, unknown> }
        return json(200, { transaction: txn({ amount: '150.00', revision: 4 }), fundClosingBalance: '150.00', duplicate: false })
      },
    })
    const router = renderAt('/txn/t1/edit')
    await screen.findByRole('heading', { name: 'Edit IJT26-2026-27-000001' })
    const amount = screen.getByLabelText(/Amount/)
    expect(amount).toHaveValue('100.00')
    await userEvent.clear(amount)
    await userEvent.type(amount, '150')
    await userEvent.click(screen.getByRole('button', { name: 'Review & save' }))
    expect(await screen.findByText('Give a reason for this change.')).toBeInTheDocument()

    await userEvent.type(screen.getByLabelText(/Reason for the change/), 'Bill said 150')
    await userEvent.click(screen.getByRole('button', { name: 'Review & save' }))
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Save changes' }))
    await screen.findByText('Updated · IJT26-2026-27-000001')
    expect(sent?.ifMatch).toBe('"3"')
    expect(sent?.body).toMatchObject({ amount: '150.00', accountId: 'a-old', categoryId: 'c-coll', paymentModeId: 'm-cash', reason: 'Bill said 150', receivedFrom: 'Yusuf' })
    expect(router.state.location.pathname).toBe('/txn/t1')
  })

  it('shows the server message when someone else saved first', async () => {
    api = base(detail({ editRequiresReason: false }), {
      'PUT /api/v1/transactions/t1': () => json(412, { code: 'REVISION_CONFLICT', title: 'This record was changed by someone else. Reload to see the latest version.' }),
    })
    renderAt('/txn/t1/edit')
    await screen.findByRole('heading', { name: 'Edit IJT26-2026-27-000001' })
    await userEvent.click(screen.getByRole('button', { name: 'Review & save' }))
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Save changes' }))
    expect(await screen.findByText(/changed by someone else/)).toBeInTheDocument()
  })
})

describe('Audit log and adjustments are Admin-only', () => {
  let api: ReturnType<typeof fakeApi> | undefined
  afterEach(() => api?.restore())

  it('lists entries for an Admin and passes the filters to the server', async () => {
    useSession.setState({ status: 'authenticated', accessToken: 't', user: adminUser })
    const urls: string[] = []
    api = fakeApi({
      'GET /api/v1/me': () => json(200, meFor(adminUser)),
      'GET /api/v1/audit-logs': (req) => {
        urls.push(req.url)
        return json(200, { items: [{ id: 9, createdAt: '2026-10-08T05:00:00Z', user: { id: 'u-admin', name: 'Asha Admin' }, action: 'TXN_CANCELLED',
          entityType: 'Transaction', entityId: 't1', fundId: 'f1', fundName: 'Ijtema 2026', oldValue: { status: 'ACTIVE' }, newValue: { status: 'CANCELLED' },
          reason: 'Entered twice', requestId: null, ipAddress: null }], nextCursor: null })
      },
    })
    renderAt('/admin/audit')
    expect(await screen.findByText('TXN_CANCELLED')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 't1' })).toHaveAttribute('href', '/txn/t1')
    await userEvent.selectOptions(screen.getByLabelText('Activity'), 'TRANSACTIONS')
    await screen.findByText('TXN_CANCELLED')
    await expect.poll(() => urls.some((u) => u.includes('group=TRANSACTIONS'))).toBe(true)
  })

  it.each(['/admin/audit', '/new/adjustment'])('does not open %s for a Member', async (path) => {
    useSession.setState({ status: 'authenticated', accessToken: 't', user: memberUser })
    api = fakeApi({ 'GET /api/v1/me': () => json(200, meFor(memberUser)) })
    renderAt(path)
    await screen.findByRole('heading', { name: /page not found/i })
    expect(api.calls.some((c) => c.url.includes('audit-logs') || c.url.includes('accounts'))).toBe(false)
  })
})
