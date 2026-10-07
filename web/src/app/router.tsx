import { createBrowserRouter, type RouteObject } from 'react-router'
import { AppShell } from './layout/AppShell'
import { PlaceholderPage } from '../pages/PlaceholderPage'
import { MorePage } from '../pages/MorePage'
import { NotFoundPage } from '../pages/NotFoundPage'

// Routes mirror the screen inventory in docs/02-App-Flow.md §1.
// Screens are placeholders in Phase 0 and are filled in by later phases.
export const routes: RouteObject[] = [
  { path: '/login', element: <PlaceholderPage screen="login" phase="Phase 1" /> },
  {
    element: <AppShell />,
    children: [
      { index: true, element: <PlaceholderPage screen="dashboard" phase="Phase 2" /> },
      { path: 'ledger', element: <PlaceholderPage screen="ledger" phase="Phase 2" /> },
      { path: 'reports', element: <PlaceholderPage screen="reports" phase="Phase 4" /> },
      { path: 'more', element: <MorePage /> },
      { path: 'new/in', element: <PlaceholderPage screen="newIn" phase="Phase 2" /> },
      { path: 'new/out', element: <PlaceholderPage screen="newOut" phase="Phase 2" /> },
      { path: 'new/transfer', element: <PlaceholderPage screen="newTransfer" phase="Phase 2" /> },
      { path: 'admin/users', element: <PlaceholderPage screen="users" phase="Phase 1" /> },
      { path: 'admin/funds', element: <PlaceholderPage screen="funds" phase="Phase 2" /> },
      { path: 'admin/accounts', element: <PlaceholderPage screen="accounts" phase="Phase 2" /> },
      { path: 'admin/categories', element: <PlaceholderPage screen="categories" phase="Phase 2" /> },
      { path: 'admin/lookups', element: <PlaceholderPage screen="lookups" phase="Phase 2" /> },
      { path: 'admin/audit', element: <PlaceholderPage screen="audit" phase="Phase 3" /> },
      { path: 'admin/settings', element: <PlaceholderPage screen="settings" phase="Phase 4" /> },
      { path: '*', element: <NotFoundPage /> },
    ],
  },
]

export const router = createBrowserRouter(routes)
