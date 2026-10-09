import { describe, expect, it } from 'vitest'
import { formatIndian, formatRupees, formatSigned, compareAmounts, groupWhileTyping, sanitizeAmount, toWireAmount } from './money'

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

describe('amount typing helpers', () => {
  it('groups live in the Indian system', () => {
    expect(groupWhileTyping('125000')).toBe('1,25,000')
    expect(groupWhileTyping('125000.5')).toBe('1,25,000.5')
    expect(groupWhileTyping('999')).toBe('999')
    expect(groupWhileTyping('1000.')).toBe('1,000.')
  })

  it('sanitizes pasted text', () => {
    expect(sanitizeAmount('₹1,25,000.567')).toBe('125000.56')
    expect(sanitizeAmount('abc')).toBe('')
    expect(sanitizeAmount('007')).toBe('7')
    expect(sanitizeAmount('1.2.3')).toBe('1.23')
  })

  it('produces the wire format or null', () => {
    expect(toWireAmount('25000')).toBe('25000.00')
    expect(toWireAmount('25000.5')).toBe('25000.50')
    expect(toWireAmount('0')).toBeNull()
    expect(toWireAmount('')).toBeNull()
    expect(toWireAmount('1.234')).toBeNull()
  })
})

describe('compareAmounts', () => {
  it('compares exactly, including negatives and different decimal lengths', () => {
    expect(compareAmounts('1500.00', '1400.00')).toBe(1)
    expect(compareAmounts('1400', '1400.00')).toBe(0)
    expect(compareAmounts('-0.01', '0')).toBe(-1)
    expect(compareAmounts('0.1', '0.10')).toBe(0)
    expect(compareAmounts('9999999999999999.99', '9999999999999999.98')).toBe(1) // beyond float precision
  })
})
