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
