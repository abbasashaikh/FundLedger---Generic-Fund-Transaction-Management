import { afterEach, describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { routes } from '../router'
import { useSession } from '../../lib/auth/session'
import { adminUser, fakeApi, json, meFor, memberUser } from '../../test/fakeApi'

vi.mock('virtual:pwa-register/react', () => ({
  useRegisterSW: () => ({ needRefresh: [false, () => {}], offlineReady: [false, () => {}], updateServiceWorker: async () => {} }),
}))

function renderAt(path: string) {
  const router = createMemoryRouter(routes, { initialEntries: [path] })
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  )
  return router
}

function signIn(user: typeof adminUser | typeof memberUser) {
  useSession.setState({ status: 'authenticated', accessToken: 'tok', user })
  return fakeApi({
    'GET /api/v1/me': () => json(200, meFor(user)),
    'GET /api/v1/version': () => json(200, { version: '1.0.0', commit: 'abcdef1234', environment: 'Testing' }),
  })
}

describe('AppShell and route guards', () => {
  let api: ReturnType<typeof fakeApi> | undefined
  afterEach(() => {
    api?.restore()
    useSession.setState({ status: 'unknown', accessToken: null, user: null })
  })

  it('sends anonymous visitors to sign-in, remembering where they were going', () => {
    useSession.setState({ status: 'anonymous', accessToken: null, user: null })
    const router = renderAt('/ledger')
    expect(router.state.location.pathname).toBe('/login')
    expect(router.state.location.search).toBe('?next=%2Fledger')
  })

  it('forces a temporary-PIN user to choose a PIN first', () => {
    useSession.setState({ status: 'authenticated', accessToken: 't', user: { ...adminUser, pinMustChange: true } })
    const router = renderAt('/')
    expect(router.state.location.pathname).toBe('/login/set-pin')
  })

  it('renders navigation, the add button and the selected fund for an Admin', async () => {
    api = signIn(adminUser)
    renderAt('/')
    expect(screen.getByRole('button', { name: 'Add transaction' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Dashboard' })).toBeInTheDocument()
    expect(await screen.findByText('Ijtema 2026')).toBeInTheDocument()
    expect(screen.getAllByRole('link', { name: 'Users' }).length).toBeGreaterThan(0)
  })

  it('hides admin navigation from Members and shows them not-found on admin URLs', async () => {
    api = signIn(memberUser)
    renderAt('/admin/users')
    expect(screen.getByRole('heading', { name: 'Page not found' })).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Users' })).not.toBeInTheDocument()
  })

  it('shows a not-found page that does not reveal record existence', () => {
    api = signIn(adminUser)
    renderAt('/txn/does-not-exist')
    expect(screen.getByRole('heading', { name: 'Page not found' })).toBeInTheDocument()
    expect(screen.getByText(/doesn't exist or you don't have access/)).toBeInTheDocument()
  })

  it('opens the quick-action sheet from the add button', async () => {
    api = signIn(adminUser)
    renderAt('/')
    await userEvent.click(screen.getByRole('button', { name: 'Add transaction' }))
    expect(screen.getByRole('button', { name: /Money In/ })).toBeInTheDocument()
  })
})
