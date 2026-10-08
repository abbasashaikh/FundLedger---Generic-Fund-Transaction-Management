import { create } from 'zustand'

export type Toast = { id: number; tone: 'success' | 'error'; message: string }
type ToastStore = { toasts: Toast[]; push: (tone: Toast['tone'], message: string) => void; dismiss: (id: number) => void }

let nextId = 1

export const useToasts = create<ToastStore>((set) => ({
  toasts: [],
  push: (tone, message) => set((s) => ({ toasts: [...s.toasts.slice(-2), { id: nextId++, tone, message }] })),
  dismiss: (id) => set((s) => ({ toasts: s.toasts.filter((t) => t.id !== id) })),
}))

export const toast = {
  success: (message: string) => useToasts.getState().push('success', message),
  error: (message: string) => useToasts.getState().push('error', message),
}

