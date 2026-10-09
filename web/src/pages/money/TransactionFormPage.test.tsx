import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { routes } from '../../app/router'
import { useSession } from '../../lib/auth/session'
import { adminUser, fakeApi, json, meFor } from '../../test/fakeApi'

const accounts = [
  { id: 'a-cash', name: 'Main Cash', kind: 'CASH', bankName: null, accountNumberLast4: null, isActive: true, sortOrder: 0 },
  { id: 'a-bank', name: 'Bank', kind: 'BANK', bankName: null, accountNumberLast4: null, isActive: true, sortOrder: 1 },
]
const modes = [
  { id: 'm-cash', name: 'Cash', requiresReference: false, isActive: true, sortOrder: 0 },
  { id: 'm-upi', name: 'UPI', requiresReference: true, isActive: true, sortOrder: 1 },
]
const catsIn = [
  { id: 'c-coll', fundId: null, direction: 'MONEY_IN', name: 'Collection', icon: null, isActive: true, sortOrder: 0 },
  { id: 'c-don', fundId: null, direction: 'MONEY_IN', name: 'Donation', icon: null, isActive: true, sortOrder: 1 },
]
const created = (amount: string) => ({
  transaction: {
    id: 't1', txnNumber: 'IJT26-2026-27-000001', type: 'DEPOSIT', status: 'ACTIVE', amount, fundId: 'f1', txnDate: '2026-10-08', txnTime: '10:00',
    createdBy: { id: 'u-admin', name: 'Asha Admin' }, createdAt: '2026-10-08T04:30:00Z', revision: 1, source: 'ONLINE',
  },
  fundClosingBalance: amount, duplicate: false,
})

function baseRoutes(extra: Record<string, (req: Request) => Response | Promise<Response>> = {}, fundOverrides: Record<string, unknown> = {}) {
  const me = meFor(adminUser)
  const fund = { ...me.funds[0]!, ...fundOverrides }
  return fakeApi({
    'GET /api/v1/me': () => json(200, { ...me, funds: [fund] }),
    'GET /api/v1/accounts': () => json(200, accounts),
    'GET /api/v1/payment-modes': () => json(200, modes),
    'GET /api/v1/categories': () => json(200, catsIn),
    'GET /api/v1/accounts/balances': () => json(200, accounts.map((a) => ({ accountId: a.id, name: a.name, kind: a.kind, closing: a.id === 'a-cash' ? '1400.00' : '30000.00' }))),
    ...extra,
  })
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

describe('Money In form', () => {
  let api: ReturnType<typeof fakeApi> | undefined
  beforeEach(() => {
    localStorage.clear()
    useSession.setState({ status: 'authenticated', accessToken: 't', user: adminUser })
  })
  afterEach(() => {
    api?.restore()
    useSession.setState({ status: 'unknown', accessToken: null, user: null })
  })

  it('groups the amount live in the Indian system and records after a confirmation step', async () => {
    api = baseRoutes({ 'POST /api/v1/transactions/deposit': () => json(201, created('125000.00')) })
    renderAt('/new/in')
    const amount = await screen.findByLabelText(/^Amount/)
    await userEvent.type(amount, '125000')
    expect(amount).toHaveValue('1,25,000')

    // Defaults come from the data: first category/account/mode, purpose pre-filled from the category.
    expect(await screen.findByRole('radio', { name: 'Collection' })).toBeChecked()
    expect(screen.getByLabelText(/^Purpose/)).toHaveValue('Collection')

    await userEvent.click(screen.getByRole('button', { name: 'Review & save' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByText('₹1,25,000.00')).toBeInTheDocument()
    expect(api.calls.some((c) => c.method === 'POST')).toBe(false)   // nothing is sent until confirmed

    await userEvent.click(within(dialog).getByRole('button', { name: 'Save Money In' }))
    expect(await screen.findByText('IJT26-2026-27-000001')).toBeInTheDocument()

    const post = api.calls.find((c) => c.method === 'POST' && c.url.endsWith('/transactions/deposit'))!
    const body = (await post.json()) as Record<string, unknown>
    expect(body).toMatchObject({ fundId: 'f1', amount: '125000.00', categoryId: 'c-coll', accountId: 'a-cash', paymentModeId: 'm-cash', purpose: 'Collection' })
    expect(body.clientTxnId).toMatch(/^[0-9a-f-]{36}$/)
  })

  it('refuses to review without an amount and shows the field error', async () => {
    api = baseRoutes()
    renderAt('/new/in')
    await screen.findByRole('radio', { name: 'Collection' })
    await userEvent.click(screen.getByRole('button', { name: 'Review & save' }))
    expect(await screen.findByText('Enter an amount greater than zero.')).toBeInTheDocument()
    expect(screen.queryByRole('dialog', { name: /Confirm/ })).not.toBeInTheDocument()
  })

  it('requires a reference number when the payment mode asks for one', async () => {
    api = baseRoutes()
    renderAt('/new/in')
    await userEvent.type(await screen.findByLabelText(/^Amount/), '500')
    await userEvent.click(await screen.findByRole('radio', { name: 'UPI' }))
    await userEvent.click(screen.getByRole('button', { name: 'Review & save' }))
    expect(await screen.findByText('UPI needs a reference number.')).toBeInTheDocument()
  })

  it('"Add another" starts a NEW entry (new client id) and keeps the chosen category and account', async () => {
    api = baseRoutes({ 'POST /api/v1/transactions/deposit': () => json(201, created('100.00')) })
    renderAt('/new/in')
    await userEvent.click(await screen.findByRole('radio', { name: 'Donation' }))
    await userEvent.type(screen.getByLabelText(/^Amount/), '100')
    await userEvent.click(screen.getByRole('button', { name: 'Review & save' }))
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Save Money In' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Add another' }))

    expect(await screen.findByRole('radio', { name: 'Donation' })).toBeChecked()
    expect(screen.getByLabelText(/^Amount/)).toHaveValue('')
    await userEvent.type(screen.getByLabelText(/^Amount/), '200')
    await userEvent.click(screen.getByRole('button', { name: 'Review & save' }))
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Save Money In' }))
    await screen.findByText('IJT26-2026-27-000001')

    const ids = api.calls.filter((c) => c.method === 'POST' && c.url.endsWith('/deposit')).map(async (c) => ((await c.json()) as { clientTxnId: string }).clientTxnId)
    const [first, second] = await Promise.all(ids)
    expect(first).not.toBe(second)
  })

  it('shows a server business error on the form instead of losing the entry', async () => {
    api = baseRoutes({
      'POST /api/v1/transactions/deposit': () => json(409, { code: 'FUND_NOT_ACTIVE', title: 'This fund is not active. New entries can\'t be added.' }),
    })
    renderAt('/new/in')
    await userEvent.type(await screen.findByLabelText(/^Amount/), '100')
    await userEvent.click(screen.getByRole('button', { name: 'Review & save' }))
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Save Money In' }))
    expect(await screen.findByRole('alert')).toHaveTextContent("This fund is not active");
    expect(screen.getByLabelText(/^Amount/)).toHaveValue('100')   // the user's input is still there
  })

  it('does not offer entry for a closed fund or without permission', async () => {
    api = baseRoutes({}, { status: 'CLOSED' })
    renderAt('/new/in')
    expect(await screen.findByText(/is closed\. New entries can't be added/)).toBeInTheDocument()
    api.restore()

    api = baseRoutes({}, { permissions: { moneyIn: false, moneyOut: true, transfer: false, viewReports: true, export: false, viewAllTransactions: true, adjust: false } })
    renderAt('/new/in')
    expect(await screen.findByText(/don't have permission to record/)).toBeInTheDocument()
  })
})

describe('Transfer form', () => {
  let api: ReturnType<typeof fakeApi> | undefined
  beforeEach(() => {
    localStorage.clear()
    useSession.setState({ status: 'authenticated', accessToken: 't', user: adminUser })
  })
  afterEach(() => api?.restore())

  it('warns (without blocking) when the transfer exceeds the source balance, and excludes the source from the destination', async () => {
    api = baseRoutes({ 'POST /api/v1/transactions/transfer': () => json(201, created('2000.00')) },
      { permissions: { moneyIn: true, moneyOut: true, transfer: true, viewReports: true, export: false, viewAllTransactions: true, adjust: false } })
    renderAt('/new/transfer')
    await userEvent.type(await screen.findByLabelText(/^Amount/), '2000')

    expect(await screen.findByText(/Main Cash has ₹1,400\.00\. This transfer will make it negative\./)).toBeInTheDocument()
    const to = screen.getByRole('radiogroup', { name: /To account/ })
    expect(within(to).queryByRole('radio', { name: /Main Cash/ })).not.toBeInTheDocument()   // BR-010
    expect(within(to).getByRole('radio', { name: /Bank/ })).toBeChecked()

    await userEvent.click(screen.getByRole('button', { name: 'Review & save' }))
    expect(await screen.findByText('Enter the purpose.')).toBeInTheDocument()
  })
})
