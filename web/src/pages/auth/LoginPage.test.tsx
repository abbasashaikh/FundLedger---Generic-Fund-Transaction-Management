import { afterEach, describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { LoginPage } from './LoginPage'
import { useSession } from '../../lib/auth/session'
import { adminUser, fakeApi, json } from '../../test/fakeApi'

function renderLogin() {
  const router = createMemoryRouter(
    [
      { path: '/login', element: <LoginPage /> },
      { path: '/', element: <p>home</p> },
      { path: '/login/set-pin', element: <p>set pin</p> },
    ],
    { initialEntries: ['/login'] },
  )
  render(<RouterProvider router={router} />)
  return router
}

describe('LoginPage', () => {
  let api: ReturnType<typeof fakeApi> | undefined
  afterEach(() => {
    api?.restore()
    useSession.setState({ status: 'unknown', accessToken: null, user: null })
  })

  it('keeps Sign in disabled until the mobile and PIN are valid', async () => {
    renderLogin()
    const button = screen.getByRole('button', { name: 'Sign in' })
    expect(button).toBeDisabled()
    await userEvent.type(screen.getByLabelText(/Mobile number/), '98765 43210')
    await userEvent.type(screen.getByLabelText(/^PIN/), '48291')
    expect(button).toBeDisabled()
    await userEvent.type(screen.getByLabelText(/^PIN/), '5')
    expect(button).toBeEnabled()
  })

  it('shows the generic credentials error and clears the PIN', async () => {
    api = fakeApi({
      'POST /api/v1/auth/login': () => json(401, { code: 'INVALID_CREDENTIALS', title: 'Mobile number or PIN is incorrect.' }),
    })
    renderLogin()
    await userEvent.type(screen.getByLabelText(/Mobile number/), '9876543210')
    await userEvent.type(screen.getByLabelText(/^PIN/), '482915')
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Mobile number or PIN is incorrect.')
    expect(screen.getByLabelText(/^PIN/)).toHaveValue('')
  })

  it('sends a temporary-PIN user to choose their own PIN', async () => {
    api = fakeApi({
      'POST /api/v1/auth/login': () => json(200, { accessToken: 't', expiresIn: 900, user: { ...adminUser, pinMustChange: true } }),
    })
    const router = renderLogin()
    await userEvent.type(screen.getByLabelText(/Mobile number/), '9876543210')
    await userEvent.type(screen.getByLabelText(/^PIN/), '482915')
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))
    await screen.findByText('set pin')
    expect(router.state.location.pathname).toBe('/login/set-pin')
  })
})
