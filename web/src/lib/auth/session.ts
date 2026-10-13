import { create } from 'zustand'
import type { components } from '../api/schema'
import { ApiError, toApiError } from '../api/errors'
import { API_BASE_URL } from '../api/config'
import { clearReadCaches } from '../offline/db'
import { loadLastSession, saveLastSession } from '../offline/refdata'

// Session handling (ADR-0002, TRD TR-014/015):
//  * the access token lives ONLY in memory (never localStorage);
//  * the refresh token is an HttpOnly cookie the browser sends to /api/v1/auth only;
//  * refreshes are single-flight within a tab and serialized across tabs (Web Locks),
//    and a REFRESH_RACE answer (another tab rotated the cookie first) is retried once.

export type SessionUser = components['schemas']['SessionProfile']
type AuthResponse = components['schemas']['AuthResponse']

type SessionState = {
  status: 'unknown' | 'anonymous' | 'authenticated'
  accessToken: string | null
  user: SessionUser | null
}

export const useSession = create<SessionState>(() => ({ status: 'unknown', accessToken: null, user: null }))

export const CLIENT_HEADER = { 'X-FundLedger-Client': 'pwa' } as const

async function postAuth(path: string, body?: unknown): Promise<Response> {
  const token = useSession.getState().accessToken
  try {
    return await fetch(`${API_BASE_URL}/api/v1/auth/${path}`, {
      method: 'POST',
      credentials: 'include',
      headers: {
        ...CLIENT_HEADER,
        ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
      },
      body: body === undefined ? undefined : JSON.stringify(body),
    })
  } catch {
    throw ApiError.network()
  }
}

async function accept(response: Response): Promise<void> {
  const auth = (await response.json()) as AuthResponse
  useSession.setState({ status: 'authenticated', accessToken: auth.accessToken, user: auth.user })
  void saveLastSession(auth.user)
}

export async function login(mobile: string, pin: string): Promise<SessionUser> {
  const response = await postAuth('login', { mobile, pin })
  if (!response.ok) throw await toApiError(response)
  await accept(response)
  return useSession.getState().user!
}

let inflight: Promise<boolean> | null = null

/** Exchanges the refresh cookie for a new access token. Resolves false when signed out. */
export function refresh(): Promise<boolean> {
  inflight ??= withCrossTabLock(doRefresh).finally(() => {
    inflight = null
  })
  return inflight
}

async function doRefresh(): Promise<boolean> {
  for (let attempt = 0; attempt < 2; attempt++) {
    let response: Response
    try {
      response = await postAuth('refresh')
    } catch {
      // Offline. Never block entry (TRD §6.3): if this device knows who signed in last, open the app as them, without a
      // token. Reads come from the device; the token is renewed when the connection is back (sync and API calls retry).
      if (useSession.getState().status !== 'unknown') return useSession.getState().status === 'authenticated'
      const remembered = await loadLastSession()
      if (remembered) {
        useSession.setState({ status: 'authenticated', accessToken: null, user: remembered })
        return true
      }

      useSession.setState({ status: 'anonymous', accessToken: null, user: null })
      return false
    }

    if (response.ok) {
      await accept(response)
      return true
    }

    const error = await toApiError(response)
    if (error.code === 'REFRESH_RACE' && attempt === 0) {
      await new Promise((r) => setTimeout(r, 300)) // the other tab has just set the new cookie
      continue
    }

    break
  }

  // The server refused the session: forget the cached profile and data. Queued entries stay, bound to their user, until that user signs in again.
  void clearReadCaches()
  useSession.setState({ status: 'anonymous', accessToken: null, user: null })
  return false
}

async function withCrossTabLock<T>(fn: () => Promise<T>): Promise<T> {
  const locks = (navigator as Navigator & { locks?: LockManager }).locks
  return locks ? locks.request('fundledger-refresh', fn) : fn()
}

export async function logout(): Promise<void> {
  try {
    await postAuth('logout')
  } finally {
    await clearReadCaches()
    useSession.setState({ status: 'anonymous', accessToken: null, user: null })
  }
}

export async function changePin(currentPin: string, newPin: string): Promise<void> {
  const response = await postAuth('pin/change', { currentPin, newPin })
  if (!response.ok) throw await toApiError(response)
  const user = useSession.getState().user
  if (user) useSession.setState({ user: { ...user, pinMustChange: false } })
}

/**
 * fetch used by the typed API client: adds the bearer token and, on a 401 from a
 * non-auth endpoint, refreshes once and retries the original request.
 */
export async function authFetch(input: Request): Promise<Response> {
  const retry = input.clone()
  const response = await send(input)
  if (response.status !== 401 || input.url.includes('/api/v1/auth/')) return response
  return (await refresh()) ? send(retry) : response
}

function send(request: Request): Promise<Response> {
  const token = useSession.getState().accessToken
  const headers = new Headers(request.headers)
  if (token) headers.set('Authorization', `Bearer ${token}`)
  return fetch(new Request(request, { headers }))
}
