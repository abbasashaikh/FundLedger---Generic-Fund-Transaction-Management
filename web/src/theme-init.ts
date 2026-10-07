// Runs before React renders, so the first paint already has the right theme
// (no light flash in dark mode). Kept tiny and dependency-free.
import { applyTheme, readThemePreference } from './lib/theme'

applyTheme(readThemePreference())
