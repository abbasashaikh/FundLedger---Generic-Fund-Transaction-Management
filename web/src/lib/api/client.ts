import createClient, { type Middleware } from 'openapi-fetch'
import type { components, paths } from './schema'
import { API_BASE_URL } from './config'
import { authFetch, CLIENT_HEADER } from '../auth/session'

/**
 * Typed API client generated from api/openapi/fundledger-api.json (P0-09).
 * Regenerate with `npm run gen:api`; CI fails if schema.d.ts is stale.
 */
export { API_BASE_URL }

const requestHeaders: Middleware = {
  onRequest({ request }) {
    request.headers.set('X-Request-Id', crypto.randomUUID().replaceAll('-', ''))
    for (const [k, v] of Object.entries(CLIENT_HEADER)) request.headers.set(k, v)
    return request
  },
}

export const api = createClient<paths>({ baseUrl: API_BASE_URL, credentials: 'include', fetch: authFetch })
api.use(requestHeaders)

export type Schemas = components['schemas']
