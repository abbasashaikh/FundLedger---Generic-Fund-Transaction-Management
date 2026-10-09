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
import { DashboardPage } from '../pages/DashboardPage'
import { LedgerPage } from '../pages/ledger/LedgerPage'
import { TransactionDetailPage } from '../pages/ledger/TransactionDetailPage'
import { TransactionFormPage } from '../pages/money/TransactionFormPage'
import { FundsPage } from '../pages/admin/FundsPage'
import { FundFormPage } from '../pages/admin/FundFormPage'
import { AccountsPage, CategoriesPage, LookupsPage } from '../pages/admin/MasterPages'

const admin = (element: React.ReactNode) => <RequireAdmin>{element}</RequireAdmin>

// Routes mirror the screen inventory in docs/02-App-Flow.md §1.
export const routes: RouteObject[] = [
  { path: '/login', element: <RedirectIfSignedIn><LoginPage /></RedirectIfSignedIn> },
  { path: '/login/set-pin', element: <RequireAuth allowPinChange><SetPinPage /></RequireAuth> },
  {
    element: <RequireAuth><AppShell /></RequireAuth>,
    children: [
      { index: true, element: <DashboardPage /> },
      { path: 'ledger', element: <LedgerPage /> },
      { path: 'txn/:id', element: <TransactionDetailPage /> },
      { path: 'reports', element: <PlaceholderPage screen="reports" phase="Phase 4" /> },
      { path: 'more', element: <MorePage /> },
      { path: 'new/in', element: <TransactionFormPage kind="in" /> },
      { path: 'new/out', element: <TransactionFormPage kind="out" /> },
      { path: 'new/transfer', element: <TransactionFormPage kind="transfer" /> },
      { path: 'admin/users', element: admin(<UsersPage />) },
      { path: 'admin/users/:id', element: admin(<UserFormPage />) },
      { path: 'admin/funds', element: admin(<FundsPage />) },
      { path: 'admin/funds/:id', element: admin(<FundFormPage />) },
      { path: 'admin/accounts', element: admin(<AccountsPage />) },
      { path: 'admin/categories', element: admin(<CategoriesPage />) },
      { path: 'admin/lookups', element: admin(<LookupsPage />) },
      { path: 'admin/audit', element: admin(<PlaceholderPage screen="audit" phase="Phase 3" />) },
      { path: 'admin/settings', element: admin(<PlaceholderPage screen="settings" phase="Phase 4" />) },
      { path: '*', element: <NotFoundPage /> },
    ],
  },
]

export const router = createBrowserRouter(routes)
