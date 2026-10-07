import createClient, { type Middleware } from 'openapi-fetch'
import type { paths } from './schema'

/**
 * Typed API client generated from api/openapi/fundledger-api.json (P0-09).
 * Regenerate with `npm run gen:api`; CI fails if schema.d.ts is stale.
 *
 * The base URL comes from VITE_API_BASE_URL at build time. It is a public URL,
 * not a secret — the PWA bundle must never contain credentials (TR-080).
 */
export const API_BASE_URL: string = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5087'

const requestId: Middleware = {
  onRequest({ request }) {
    request.headers.set('X-Request-Id', crypto.randomUUID().replaceAll('-', ''))
    return request
  },
}

export const api = createClient<paths>({ baseUrl: API_BASE_URL, credentials: 'include' })
api.use(requestId)
