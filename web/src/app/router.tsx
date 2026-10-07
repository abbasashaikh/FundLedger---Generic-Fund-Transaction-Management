import { createBrowserRouter, type RouteObject } from 'react-router'
import { AppShell } from './layout/AppShell'
import { RedirectIfSignedIn, RequireAdmin, RequireAuth } from './guards'
import { PlaceholderPage } from '../pages/PlaceholderPage'
import { MorePage } from '../pages/MorePage'
import { NotFoundPage } from '../pages/NotFoundPage'
import { LoginPage } from '../pages/auth/LoginPage'
import { SetPinPage } from '../pages/auth/SetPinPage'
import { UsersPage } from '../pages/admin/UsersPage'
import { UserFormPage } from '../pages/admin/UserFormPage'

const admin = (element: React.ReactNode) => <RequireAdmin>{element}</RequireAdmin>

// Routes mirror the screen inventory in docs/02-App-Flow.md §1.
export const routes: RouteObject[] = [
  { path: '/login', element: <RedirectIfSignedIn><LoginPage /></RedirectIfSignedIn> },
  { path: '/login/set-pin', element: <RequireAuth allowPinChange><SetPinPage /></RequireAuth> },
  {
    element: <RequireAuth><AppShell /></RequireAuth>,
    children: [
      { index: true, element: <PlaceholderPage screen="dashboard" phase="Phase 2" /> },
      { path: 'ledger', element: <PlaceholderPage screen="ledger" phase="Phase 2" /> },
      { path: 'reports', element: <PlaceholderPage screen="reports" phase="Phase 4" /> },
      { path: 'more', element: <MorePage /> },
      { path: 'new/in', element: <PlaceholderPage screen="newIn" phase="Phase 2" /> },
      { path: 'new/out', element: <PlaceholderPage screen="newOut" phase="Phase 2" /> },
      { path: 'new/transfer', element: <PlaceholderPage screen="newTransfer" phase="Phase 2" /> },
      { path: 'admin/users', element: admin(<UsersPage />) },
      { path: 'admin/users/:id', element: admin(<UserFormPage />) },
      { path: 'admin/funds', element: admin(<PlaceholderPage screen="funds" phase="Phase 2" />) },
      { path: 'admin/accounts', element: admin(<PlaceholderPage screen="accounts" phase="Phase 2" />) },
      { path: 'admin/categories', element: admin(<PlaceholderPage screen="categories" phase="Phase 2" />) },
      { path: 'admin/lookups', element: admin(<PlaceholderPage screen="lookups" phase="Phase 2" />) },
      { path: 'admin/audit', element: admin(<PlaceholderPage screen="audit" phase="Phase 3" />) },
      { path: 'admin/settings', element: admin(<PlaceholderPage screen="settings" phase="Phase 4" />) },
      { path: '*', element: <NotFoundPage /> },
    ],
  },
]

export const router = createBrowserRouter(routes)
