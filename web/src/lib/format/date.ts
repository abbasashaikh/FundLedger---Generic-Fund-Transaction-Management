// Dates/times are shown in the ORGANIZATION's timezone, never the device's (Design Brief §4).
// V1 organizations default to Asia/Kolkata; the value comes from /me when available.

let orgTimeZone = 'Asia/Kolkata'

export function setOrganizationTimeZone(tz: string | undefined): void {
  if (tz) orgTimeZone = tz
}

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

/** "05-Oct-2026 10:00 am" in the organization's timezone. */
export function formatDateTime(iso: string): string {
  const parts = new Intl.DateTimeFormat('en-GB', {
    timeZone: orgTimeZone, day: '2-digit', month: 'numeric', year: 'numeric', hour: 'numeric', minute: '2-digit', hour12: true,
  }).formatToParts(new Date(iso))
  const get = (type: string) => parts.find((p) => p.type === type)?.value ?? ''
  const month = MONTHS[Number(get('month')) - 1] ?? ''
  return `${get('day')}-${month}-${get('year')} ${get('hour')}:${get('minute')} ${get('dayPeriod').toLowerCase()}`
}
