// All money/date formatting goes through this module (Design Brief §4).
// Components must never call toLocaleString directly.
//
// Amounts arrive from the API as decimal STRINGS ("25000.00", TRD §11.1) and are
// formatted without converting to a JS number, so no float rounding can occur.

const GROUP_RE = /\B(?=(\d{2})+(?!\d))/g

/** "125000.5" -> "1,25,000.50" (Indian lakh/crore grouping). */
export function formatIndian(amount: string, opts: { decimals?: 'always' | 'auto' } = {}): string {
  const match = /^(-)?(\d+)(?:\.(\d{1,2}))?$/.exec(amount.trim())
  if (!match) throw new Error(`Not a decimal amount: ${amount}`)
  const [, minus = '', intPart = '0', frac = ''] = match
  const fraction = frac.padEnd(2, '0')

  const head = intPart.slice(0, -3)
  const tail = intPart.slice(-3)
  const grouped = head ? `${head.replace(GROUP_RE, ',')},${tail}` : tail

  const showDecimals = opts.decimals !== 'auto' || fraction !== '00'
  return `${minus}${grouped}${showDecimals ? `.${fraction}` : ''}`
}

/** "₹1,25,000" in lists (whole amounts drop .00), "₹1,25,000.00" in detail. */
export function formatRupees(amount: string, opts: { decimals?: 'always' | 'auto' } = {}): string {
  const negative = amount.trim().startsWith('-')
  const body = formatIndian(negative ? amount.trim().slice(1) : amount, opts)
  return negative ? `−₹${body}` : `₹${body}`
}

export type TxnType = 'DEPOSIT' | 'EXPENSE' | 'TRANSFER' | 'ADJUSTMENT'

/** Signed display with the type glyph: "+ ₹25,000", "− ₹8,500", "⇄ ₹20,000". */
export function formatSigned(type: TxnType, amount: string, adjustment?: 'INCREASE' | 'DECREASE'): string {
  const value = formatRupees(amount, { decimals: 'auto' })
  switch (type) {
    case 'DEPOSIT':
      return `+ ${value}`
    case 'EXPENSE':
      return `− ${value}`
    case 'TRANSFER':
      return `⇄ ${value}`
    case 'ADJUSTMENT':
      return `${adjustment === 'DECREASE' ? '−' : '+'} ${value}`
  }
}

/** Live grouping for an amount being typed: "125000.5" -> "1,25,000.5" (integer part only; keeps what the user typed after the dot). */
export function groupWhileTyping(raw: string): string {
  const [intPart = '', frac] = raw.split('.')
  const head = intPart.slice(0, -3)
  const grouped = head ? `${head.replace(GROUP_RE, ',')},${intPart.slice(-3)}` : intPart
  return frac === undefined ? grouped : `${grouped}.${frac}`
}

/** Sanitizes pasted/typed text to a plain amount: digits and one dot, at most 2 decimals ("₹1,25,000.567" -> "125000.56"). */
export function sanitizeAmount(text: string): string {
  const cleaned = text.replace(/[^\d.]/g, '')
  const dot = cleaned.indexOf('.')
  const intPart = (dot === -1 ? cleaned : cleaned.slice(0, dot)).replace(/^0+(?=\d)/, '')
  if (dot === -1) return intPart
  return `${intPart}.${cleaned.slice(dot + 1).replace(/\./g, '').slice(0, 2)}`
}

/** Normalizes a typed amount to the wire format "25000.00", or null when it is not a positive amount. */
export function toWireAmount(raw: string): string | null {
  if (!/^\d+(\.\d{0,2})?$/.test(raw)) return null
  const [i = '0', f = ''] = raw.split('.')
  const wire = `${i}.${f.padEnd(2, '0')}`
  return /[1-9]/.test(wire) ? wire : null   // positive: any non-zero digit (no float maths on money)
}

function toPaise(amount: string): bigint {
  const negative = amount.trim().startsWith('-')
  const [i = '0', f = ''] = amount.trim().replace(/^-/, '').split('.')
  const paise = BigInt(`${i}${f.padEnd(2, '0').slice(0, 2)}`)
  return negative ? -paise : paise
}

/** Exact comparison of two decimal-string amounts (-1, 0, 1) without floating point. */
export function compareAmounts(a: string, b: string): -1 | 0 | 1 {
  const x = toPaise(a)
  const y = toPaise(b)
  return x < y ? -1 : x > y ? 1 : 0
}
