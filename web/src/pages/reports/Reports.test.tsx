import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { routes } from '../../app/router'
import { useSession } from '../../lib/auth/session'
import { adminUser, fakeApi, json, meFor } from '../../test/fakeApi'

const catalog = [
  { code: 'FUND_SUMMARY', title: 'Fund summary', description: 'Opening, in, out and closing.' },
  { code: 'MONEY_IN', title: 'Money in', description: 'Every money-in entry.' },
]

const report = {
  code: 'MONEY_IN', title: 'Money in', description: '', fundId: 'f1', fundName: 'Ijtema 2026', from: '2026-10-01', to: '2026-10-09', ownOnly: false,
  generatedAt: '2026-10-09T05:00:00Z', generatedBy: 'Asha Admin', organizationName: 'Al Madad',
  summary: [{ key: 'total', label: 'Total money in', value: '125000.50', kind: 'MONEY' }, { key: 'count', label: 'Entries', value: '2', kind: 'NUMBER' }],
  columns: [
    { key: 'date', label: 'Date', kind: 'DATE' }, { key: 'purpose', label: 'Purpose', kind: 'TEXT' }, { key: 'amount', label: 'Amount', kind: 'MONEY' },
  ],
  rows: [{ date: '2026-10-08', purpose: 'Collection', amount: '125000.50' }],
  totals: { date: 'Total', amount: '125000.50' }, truncated: false,
}

function meWith(perms: Record<string, boolean>) {
  const me = meFor(adminUser)
  return { ...me, funds: [{ ...me.funds[0]!, permissions: { ...me.funds[0]!.permissions, ...perms } }] }
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

describe('Reports', () => {
  let api: ReturnType<typeof fakeApi> | undefined
  beforeEach(() => useSession.setState({ status: 'authenticated', accessToken: 't', user: adminUser }))
  afterEach(() => api?.restore())

  it('lists the reports and links to each', async () => {
    api = fakeApi({ 'GET /api/v1/me': () => json(200, meWith({ viewReports: true, export: true })), 'GET /api/v1/reports': () => json(200, catalog) })
    renderAt('/reports')
    expect(await screen.findByRole('link', { name: /Fund summary/ })).toHaveAttribute('href', '/reports/FUND_SUMMARY')
    expect(screen.getByRole('link', { name: /My downloads/ })).toBeInTheDocument()
  })

  it('shows no report cards without the can_view_reports permission', async () => {
    api = fakeApi({ 'GET /api/v1/me': () => json(200, meWith({ viewReports: false })), 'GET /api/v1/reports': () => json(200, catalog) })
    renderAt('/reports')
    expect(await screen.findByText("You don't have access to reports for this fund.")).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: /Fund summary/ })).not.toBeInTheDocument()
  })

  it('shows the summary and rows with Indian grouping, and labels a limited scope', async () => {
    const urls: string[] = []
    api = fakeApi({
      'GET /api/v1/me': () => json(200, meWith({ viewReports: true })),
      'GET /api/v1/reports/MONEY_IN': (req) => { urls.push(req.url); return json(200, { ...report, ownOnly: true }) },
      'GET /api/v1/categories': () => json(200, []),
      'GET /api/v1/accounts': () => json(200, []),
    })
    renderAt('/reports/MONEY_IN')
    expect((await screen.findAllByText('₹1,25,000.50')).length).toBeGreaterThan(1)       // summary card + table rows
    expect(screen.getByText('08-Oct-2026', { selector: 'td' })).toBeInTheDocument()
    expect(screen.getByText('My transactions only')).toBeInTheDocument()
    expect(urls[0]).toContain('fundId=f1')
    expect(urls[0]).toMatch(/from=\d{4}-\d{2}-01/)                                          // "This month" is the default

    await userEvent.click(screen.getByLabelText('Today'))
    await expect.poll(() => urls.length).toBeGreaterThan(1)
    const last = new URL(urls.at(-1)!)
    expect(last.searchParams.get('from')).toBe(last.searchParams.get('to'))
  })

  it('exports with an Idempotency-Key, waits for the file, then offers the download', async () => {
    let created: { key: string | null; body: unknown } | undefined
    let polls = 0
    const job = (status: string) => ({
      id: 'job1', reportCode: 'MONEY_IN', format: 'CSV', status, rowCount: status === 'SUCCEEDED' ? 3 : null, errorCode: null, createdAt: '2026-10-09T05:00:00Z',
      finishedAt: null, expiresAt: null, fileName: 'money-in.csv', fundId: 'f1',
    })
    api = fakeApi({
      'GET /api/v1/me': () => json(200, meWith({ viewReports: true, export: true })),
      'GET /api/v1/reports/MONEY_IN': () => json(200, report),
      'GET /api/v1/categories': () => json(200, []),
      'GET /api/v1/accounts': () => json(200, []),
      'POST /api/v1/exports': async (req) => { created = { key: req.headers.get('Idempotency-Key'), body: await req.json() }; return json(202, job('QUEUED')) },
      'GET /api/v1/exports/job1': () => json(200, job(++polls < 2 ? 'RUNNING' : 'SUCCEEDED')),
    })
    renderAt('/reports/MONEY_IN')
    await userEvent.click(await screen.findByRole('button', { name: 'Export' }))
    const dialog = await screen.findByRole('dialog')
    await userEvent.click(within(dialog).getByLabelText('CSV'))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Export' }))
    expect(await within(dialog).findByText('Preparing your report…')).toBeInTheDocument()
    expect(await within(dialog).findByRole('button', { name: 'Download' }, { timeout: 5000 })).toBeInTheDocument()
    expect(created?.key).toMatch(/^[0-9a-f-]{36}$/)
    expect(created?.body).toMatchObject({ reportCode: 'MONEY_IN', format: 'CSV', fundId: 'f1' })
  })

  it('hides the Export button without the export permission', async () => {
    api = fakeApi({
      'GET /api/v1/me': () => json(200, meWith({ viewReports: true, export: false })),
      'GET /api/v1/reports/MONEY_IN': () => json(200, report),
      'GET /api/v1/categories': () => json(200, []),
      'GET /api/v1/accounts': () => json(200, []),
    })
    renderAt('/reports/MONEY_IN')
    await screen.findByText('Total money in')
    expect(screen.queryByRole('button', { name: 'Export' })).not.toBeInTheDocument()
  })

  it('downloads a receipt for a Money In entry', async () => {
    const detail = {
      transaction: {
        id: 't1', txnNumber: 'IJT26-2026-27-000001', type: 'DEPOSIT', status: 'ACTIVE', amount: '100.00', fundId: 'f1', txnDate: '2026-10-08', txnTime: '10:00',
        category: null, account: null, fromAccount: null, toAccount: null, paymentMode: null, adjustmentDirection: null, receivedFrom: 'Yusuf', paidTo: null, purpose: 'Collection',
        referenceNumber: null, remarks: null, createdBy: { id: 'u', name: 'Asha' }, createdAt: '2026-10-08T04:30:00Z', revision: 1, source: 'ONLINE',
        updatedBy: null, updatedAt: null, cancelledBy: null, cancelledAt: null, cancellationReason: null,
      },
      canEdit: false, canCancel: false, editableUntil: null, editRequiresReason: false,
    }
    URL.createObjectURL = vi.fn(() => 'blob:x')
    URL.revokeObjectURL = vi.fn()
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    api = fakeApi({
      'GET /api/v1/me': () => json(200, meWith({})),
      'GET /api/v1/transactions/t1': () => json(200, detail),
      'GET /api/v1/transactions/t1/history': () => json(200, []),
      'GET /api/v1/transactions/t1/receipt.pdf': () => new Response('%PDF-1.4', { status: 200, headers: { 'Content-Type': 'application/pdf', 'Content-Disposition': 'attachment; filename="receipt-x.pdf"' } }),
    })
    renderAt('/txn/t1')
    await userEvent.click(await screen.findByRole('button', { name: 'Receipt (PDF)' }))
    await expect.poll(() => api!.calls.some((c) => c.url.endsWith('/receipt.pdf'))).toBe(true)
    await expect.poll(() => (URL.createObjectURL as ReturnType<typeof vi.fn>).mock.calls.length).toBeGreaterThan(0)
  })
})

describe('Settings', () => {
  let api: ReturnType<typeof fakeApi> | undefined
  beforeEach(() => useSession.setState({ status: 'authenticated', accessToken: 't', user: adminUser }))
  afterEach(() => api?.restore())

  const settings = {
    organization: { name: 'Al Madad', contactMobile: null, contactEmail: null, address: null, registrationNumber: null, currencyCode: 'INR', timezone: 'Asia/Kolkata', dateFormat: 'dd-MMM-yyyy' },
    transactions: { editWindowMinutes: 15, backdateDaysMember: 7, maxAmount: '1000000.00' },
    auth: { pinMaxFailures: 5, pinLockoutMinutes: 15, sessionIdleMinutes: 480, sessionAbsoluteDays: 7 },
    receipts: { enabled: true, footerText: 'Thanks.', showRecordedBy: true },
    offline: { enabled: true, maxQueueAgeHours: 72 },
  }

  it('lists what will change before saving, then saves', async () => {
    let put: unknown
    api = fakeApi({
      'GET /api/v1/me': () => json(200, meFor(adminUser)),
      'GET /api/v1/settings': () => json(200, settings),
      'PUT /api/v1/settings': async (req) => { put = await req.json(); return json(200, { ...settings, transactions: { ...settings.transactions, editWindowMinutes: 30 } }) },
    })
    renderAt('/admin/settings')
    const field = await screen.findByLabelText('Edit window (minutes)')
    expect(screen.getByRole('button', { name: 'Save changes' })).toBeDisabled()          // nothing changed yet
    await userEvent.clear(field)
    await userEvent.type(field, '30')
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByText('15')).toBeInTheDocument()
    expect(within(dialog).getByText('30')).toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save changes' }))
    await screen.findByText('Settings saved.')
    expect(put).toMatchObject({ transactions: { editWindowMinutes: 30, maxAmount: '1000000.00' } })
  })
})
