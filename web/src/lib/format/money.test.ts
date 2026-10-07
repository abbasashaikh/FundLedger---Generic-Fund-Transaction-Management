import { describe, expect, it } from 'vitest'
import { formatIndian, formatRupees, formatSigned } from './money'

describe('formatIndian', () => {
  it.each([
    ['0', '0.00'],
    ['999', '999.00'],
    ['1000', '1,000.00'],
    ['125000', '1,25,000.00'],
    ['1000000.5', '10,00,000.50'],
    ['12345678.90', '1,23,45,678.90'],
    ['-8500', '-8,500.00'],
  ])('%s -> %s', (input, expected) => {
    expect(formatIndian(input)).toBe(expected)
  })

  it('drops .00 in auto mode but keeps real paise', () => {
    expect(formatIndian('25000.00', { decimals: 'auto' })).toBe('25,000')
    expect(formatIndian('25000.50', { decimals: 'auto' })).toBe('25,000.50')
  })

  it('rejects non-decimal input instead of guessing', () => {
    expect(() => formatIndian('1,000')).toThrow()
    expect(() => formatIndian('abc')).toThrow()
  })
})

describe('formatRupees / formatSigned', () => {
  it('prefixes the rupee sign and uses a typographic minus', () => {
    expect(formatRupees('31400.00')).toBe('₹31,400.00')
    expect(formatRupees('-100')).toBe('−₹100.00')
  })

  it('shows a glyph per transaction type', () => {
    expect(formatSigned('DEPOSIT', '25000.00')).toBe('+ ₹25,000')
    expect(formatSigned('EXPENSE', '8500.00')).toBe('− ₹8,500')
    expect(formatSigned('TRANSFER', '20000.00')).toBe('⇄ ₹20,000')
    expect(formatSigned('ADJUSTMENT', '100.00', 'DECREASE')).toBe('− ₹100')
  })
})
