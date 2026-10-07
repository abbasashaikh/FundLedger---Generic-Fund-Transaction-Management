import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { authFetch, login, refresh, useSession } from './session'
import { ApiError } from '../api/errors'
import { adminUser, fakeApi, json } from '../../test/fakeApi'

const auth = (token: string) => ({ accessToken: token, expiresIn: 900, user: adminUser })

describe('session', () => {
  let api: ReturnType<typeof fakeApi> | undefined
  beforeEach(() => useSession.setState({ status: 'unknown', accessToken: null, user: null }))
  afterEach(() => api?.restore())

  it('login keeps the access token in memory only and sends the CSRF client header', async () => {
    api = fakeApi({ 'POST /api/v1/auth/login': () => json(200, auth('t1')) })
    await login('9876543210', '482915')

    expect(useSession.getState()).toMatchObject({ status: 'authenticated', accessToken: 't1' })
    expect(api.calls[0]!.headers.get('X-FundLedger-Client')).toBe('pwa')
    expect(api.calls[0]!.credentials).toBe('include')
    expect(JSON.stringify(localStorage)).not.toContain('t1')
  })

  it('login surfaces the server problem as ApiError with retry-after', async () => {
    api = fakeApi({
      'POST /api/v1/auth/login': () =>
        json(429, { code: 'ACCOUNT_LOCKED', title: 'Too many attempts.' }, { 'Retry-After': '600' }),
    })
    const error = await login('9876543210', '000000').catch((e: unknown) => e)
    expect(error).toBeInstanceOf(ApiError)
    expect(error).toMatchObject({ code: 'ACCOUNT_LOCKED', retryAfterSeconds: 600 })
  })

  it('authFetch refreshes once on 401 and retries the original request', async () => {
    useSession.setState({ status: 'authenticated', accessToken: 'expired', user: adminUser })
    api = fakeApi({
      'GET /api/v1/me': (req) =>
        req.headers.get('Authorization') === 'Bearer fresh' ? json(200, { ok: true }) : json(401, { code: 'UNAUTHENTICATED' }),
      'POST /api/v1/auth/refresh': () => json(200, auth('fresh')),
    })

    const response = await authFetch(new Request('http://localhost:5087/api/v1/me'))
    expect(response.status).toBe(200)
    expect(api.calls.map((c) => new URL(c.url).pathname)).toEqual(['/api/v1/me', '/api/v1/auth/refresh', '/api/v1/me'])
  })

  it('a parallel-tab REFRESH_RACE is retried once instead of signing the user out', async () => {
    let attempts = 0
    api = fakeApi({
      'POST /api/v1/auth/refresh': () =>
        ++attempts === 1 ? json(401, { code: 'REFRESH_RACE' }) : json(200, auth('after-race')),
    })
    expect(await refresh()).toBe(true)
    expect(useSession.getState().accessToken).toBe('after-race')
  })

  it('concurrent refresh calls share one request', async () => {
    api = fakeApi({ 'POST /api/v1/auth/refresh': () => json(200, auth('one')) })
    await Promise.all([refresh(), refresh(), refresh()])
    expect(api.calls.length).toBe(1)
  })

  it('a rejected refresh signs the user out locally', async () => {
    useSession.setState({ status: 'authenticated', accessToken: 'x', user: adminUser })
    api = fakeApi({ 'POST /api/v1/auth/refresh': () => json(401, { code: 'UNAUTHENTICATED' }) })
    expect(await refresh()).toBe(false)
    expect(useSession.getState()).toMatchObject({ status: 'anonymous', accessToken: null, user: null })
  })
})
