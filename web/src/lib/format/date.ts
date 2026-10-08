// Dates/times are shown in the ORGANIZATION's timezone, never the device's (Design Brief §4).
// V1 organizations default to Asia/Kolkata; the value comes from /me when available.

let orgTimeZone = 'Asia/Kolkata'

export function setOrganizationTimeZone(tz: string | undefined): void {
  if (tz) orgTimeZone = tz
}

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

type Parts = Record<string, string>

function partsOf(date: Date, options: Intl.DateTimeFormatOptions): Parts {
  const out: Parts = {}
  for (const p of new Intl.DateTimeFormat('en-GB', { timeZone: orgTimeZone, ...options }).formatToParts(date)) out[p.type] = p.value
  return out
}

/** "05-Oct-2026 10:00 am" in the organization's timezone. */
export function formatDateTime(iso: string): string {
  const p = partsOf(new Date(iso), { day: '2-digit', month: 'numeric', year: 'numeric', hour: 'numeric', minute: '2-digit', hour12: true })
  return `${p.day}-${MONTHS[Number(p.month) - 1] ?? ''}-${p.year} ${p.hour}:${p.minute} ${(p.dayPeriod ?? '').toLowerCase()}`
}

/** Today's date in the organization's timezone as YYYY-MM-DD (what the server calls "today"). */
export function todayIso(now: Date = new Date()): string {
  const p = partsOf(now, { year: 'numeric', month: '2-digit', day: '2-digit' })
  return `${p.year}-${p.month}-${p.day}`
}

/** The current time in the organization's timezone as HH:mm. */
export function nowTime(now: Date = new Date()): string {
  const p = partsOf(now, { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' })
  return `${p.hour}:${p.minute}`
}

/** Adds whole days to a YYYY-MM-DD date (calendar arithmetic, no timezone involved). */
export function addDays(iso: string, days: number): string {
  const [y, m, d] = iso.split('-').map(Number) as [number, number, number]
  const date = new Date(Date.UTC(y, m - 1, d + days))
  return date.toISOString().slice(0, 10)
}

/** "05-Oct-2026" from YYYY-MM-DD. */
export function formatDate(iso: string): string {
  const [y, m, d] = iso.split('-')
  return `${d}-${MONTHS[Number(m) - 1] ?? ''}-${y}`
}

/** Ledger date header: "Today", "Yesterday" or "Mon, 05-Oct" (Design Brief §4). */
export function dayHeading(iso: string, today: string = todayIso()): string {
  if (iso === today) return 'Today'
  if (iso === addDays(today, -1)) return 'Yesterday'
  const [y, m, d] = iso.split('-').map(Number) as [number, number, number]
  const weekday = new Date(Date.UTC(y, m - 1, d)).toLocaleDateString('en-GB', { weekday: 'short', timeZone: 'UTC' })
  return `${weekday}, ${String(d).padStart(2, '0')}-${MONTHS[m - 1] ?? ''}${y === Number(today.slice(0, 4)) ? '' : `-${y}`}`
}

/** "10:00 am" from HH:mm. */
export function formatTime(hhmm: string): string {
  const [h, m] = hhmm.split(':').map(Number) as [number, number]
  return `${h % 12 === 0 ? 12 : h % 12}:${String(m).padStart(2, '0')} ${h < 12 ? 'am' : 'pm'}`
}

/** First day of the month/week for filter presets, in the org timezone. */
export function startOfMonth(today: string = todayIso()): string {
  return `${today.slice(0, 8)}01`
}

export function startOfWeek(today: string = todayIso()): string {
  const [y, m, d] = today.split('-').map(Number) as [number, number, number]
  const dow = new Date(Date.UTC(y, m - 1, d)).getUTCDay() // 0 = Sunday
  return addDays(today, -((dow + 6) % 7)) // weeks start on Monday
}
