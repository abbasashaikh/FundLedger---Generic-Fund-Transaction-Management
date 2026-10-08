import { vi } from 'vitest'

type Handler = (req: Request) => Response | Promise<Response>

/** Replaces global fetch with a tiny router keyed by "METHOD /path". Unmatched → 404. */
export function fakeApi(routes: Record<string, Handler>) {
  const calls: Request[] = []
  const spy = vi.spyOn(globalThis, 'fetch').mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
    const req = input instanceof Request ? input : new Request(input, init)
    calls.push(req.clone())
    const key = `${req.method} ${new URL(req.url).pathname}`
    const handler = routes[key]
    return handler ? handler(req) : json(404, { code: 'NOT_FOUND', title: 'Not found.' })
  })
  return { calls, restore: () => spy.mockRestore() }
}

export function json(status: number, body: unknown, headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json', ...headers } })
}

export const adminUser = { id: 'u-admin', organizationId: 'o1', fullName: 'Asha Admin', role: 'ADMIN', pinMustChange: false } as const
export const memberUser = { id: 'u-member', organizationId: 'o1', fullName: 'Imran Member', role: 'MEMBER', pinMustChange: false } as const

export function meFor(user: { id: string; fullName: string; role: 'ADMIN' | 'MEMBER' }) {
  return {
    id: user.id, fullName: user.fullName, mobile: '+919876543210', role: user.role, pinMustChange: false,
    organization: { id: 'o1', name: 'Al Madad', shortCode: 'ALM', currencyCode: 'INR', timezone: 'Asia/Kolkata', dateFormat: 'dd-MMM-yyyy', receiptsEnabled: true },
    funds: [{ id: 'f1', code: 'IJT26', name: 'Ijtema 2026', status: 'ACTIVE',
      permissions: { moneyIn: true, moneyOut: true, transfer: false, viewReports: true, export: false, viewAllTransactions: true, adjust: false } }],
  }
}
