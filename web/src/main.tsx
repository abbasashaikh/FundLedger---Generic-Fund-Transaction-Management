import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { RouterProvider } from 'react-router'
import './styles/index.css'
import './i18n'
import { ApiError } from './lib/api/errors'
import { router } from './app/router'
import { SessionBootstrap } from './app/guards'

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      // Never retry client errors (401/403/404/409): only transient failures.
      retry: (count, error) => count < 1 && !(error instanceof ApiError && error.status >= 400 && error.status < 500),
      staleTime: 30_000,
      refetchOnWindowFocus: true,
    },
  },
})

const root = document.getElementById('root')
if (!root) throw new Error('Root element #root missing')

createRoot(root).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <SessionBootstrap>
        <RouterProvider router={router} />
      </SessionBootstrap>
    </QueryClientProvider>
  </StrictMode>,
)
