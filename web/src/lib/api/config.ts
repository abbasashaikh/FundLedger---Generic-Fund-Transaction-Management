/**
 * API origin, from VITE_API_BASE_URL at build time. A public URL, not a secret —
 * the PWA bundle must never contain credentials (TR-080).
 */
export const API_BASE_URL: string = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5087'
