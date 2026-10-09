// Writes dist/_headers for Cloudflare Pages: security headers + a CSP whose
// connect-src is the API origin of THIS build (TRD TR-072). Runs after `vite build`.
import { writeFileSync } from 'node:fs'

const api = process.env.VITE_API_BASE_URL ?? 'http://localhost:5087'
const apiOrigin = new URL(api).origin
const sentry = 'https://*.ingest.sentry.io https://*.ingest.de.sentry.io'

const csp = [
  "default-src 'self'",
  "script-src 'self'",
  "style-src 'self'",
  "img-src 'self' data: blob:",
  "font-src 'self'",
  `connect-src 'self' ${apiOrigin} ${sentry}`,
  "worker-src 'self'",
  "manifest-src 'self'",
  "base-uri 'self'",
  "form-action 'self'",
  "frame-ancestors 'none'",
  "object-src 'none'",
].join('; ')

const headers = `/*
  Content-Security-Policy: ${csp}
  X-Content-Type-Options: nosniff
  Referrer-Policy: strict-origin-when-cross-origin
  Permissions-Policy: camera=(self), microphone=(), geolocation=(), payment=()
  Strict-Transport-Security: max-age=31536000; includeSubDomains

/sw.js
  Cache-Control: no-cache

/index.html
  Cache-Control: no-cache

/assets/*
  Cache-Control: public, max-age=31536000, immutable
`

writeFileSync(new URL('../dist/_headers', import.meta.url), headers)
console.log(`dist/_headers written (connect-src ${apiOrigin})`)
