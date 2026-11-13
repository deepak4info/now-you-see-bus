import { lazy, Suspense } from "react";
import { Navigate, Outlet, Route, Routes, useLocation } from "react-router-dom";
import type { ReactNode } from "react";
import { ErrorBoundary } from "@/components/ErrorBoundary";
import { Shell } from "@/components/Shell";
import { useAuth } from "@/lib/auth";

// Public & Passenger Pages (Lazy Loaded for minimal initial bundle)
const HomePage = lazy(() => import("@/pages/Home").then(m => ({ default: m.HomePage })));
const LiveRoutesPage = lazy(() => import("@/pages/LiveRoutes").then(m => ({ default: m.LiveRoutesPage })));
const EnquiryNumbersPage = lazy(() => import("@/pages/EnquiryNumbers").then(m => ({ default: m.EnquiryNumbersPage })));
const SearchPage = lazy(() => import("@/pages/Search").then(m => ({ default: m.SearchPage })));
const BusPage = lazy(() => import("@/pages/Bus").then(m => ({ default: m.BusPage })));
const TrackPage = lazy(() => import("@/pages/Track").then(m => ({ default: m.TrackPage })));
const LoginPage = lazy(() => import("@/pages/Login").then(m => ({ default: m.LoginPage })));
const RegisterPage = lazy(() => import("@/pages/Register").then(m => ({ default: m.RegisterPage })));
const FavoritesPage = lazy(() => import("@/pages/Favorites").then(m => ({ default: m.FavoritesPage })));
const NotificationsPage = lazy(() => import("@/pages/Notifications").then(m => ({ default: m.NotificationsPage })));
const DriverPage = lazy(() => import("@/pages/Driver").then(m => ({ default: m.DriverPage })));
const StopTimetablePage = lazy(() => import("@/pages/StopTimetable").then(m => ({ default: m.StopTimetablePage })));
const AiAssistantPage = lazy(() => import("@/pages/AiAssistantPage").then(m => ({ default: m.AiAssistantPage })));

// Admin Management Suite (Code-Split into separate isolated chunk)
const AdminShell = lazy(() => import("@/pages/admin/AdminShell").then(m => ({ default: m.AdminShell })));
const OverviewPage = lazy(() => import("@/pages/admin/Overview").then(m => ({ default: m.OverviewPage })));
const BulkUploadPage = lazy(() => import("@/pages/admin/BulkUpload").then(m => ({ default: m.BulkUploadPage })));
const WorkflowsPage = lazy(() => import("@/pages/admin/Workflows").then(m => ({ default: m.WorkflowsPage })));
const OperatorsPage = lazy(() => import("@/pages/admin/DataPages").then(m => ({ default: m.OperatorsPage })));
const BusesPage = lazy(() => import("@/pages/admin/DataPages").then(m => ({ default: m.BusesPage })));
const DriversPage = lazy(() => import("@/pages/admin/DataPages").then(m => ({ default: m.DriversPage })));
const StopsPage = lazy(() => import("@/pages/admin/DataPages").then(m => ({ default: m.StopsPage })));
const RoutesPage = lazy(() => import("@/pages/admin/DataPages").then(m => ({ default: m.RoutesPage })));
const SchedulesPage = lazy(() => import("@/pages/admin/DataPages").then(m => ({ default: m.SchedulesPage })));
const LivePage = lazy(() => import("@/pages/admin/DataPages").then(m => ({ default: m.LivePage })));
const ReportsPage = lazy(() => import("@/pages/admin/DataPages").then(m => ({ default: m.ReportsPage })));

function PageLoader() {
  return (
    <div className="flex min-h-[50vh] flex-col items-center justify-center p-8 text-center" aria-live="polite">
      <div className="h-8 w-8 animate-spin rounded-full border-2 border-primary border-t-transparent" />
      <p className="mt-3 text-sm text-muted-foreground animate-pulse">Loading view…</p>
    </div>
  );
}

function Guard({ roles, children }: { roles?: string[]; children: ReactNode }) {
  const { user, loading } = useAuth();
  const location = useLocation();
  if (loading) return <PageLoader />;
  if (!user) return <Navigate to={`/login?next=${encodeURIComponent(location.pathname)}`} replace />;
  if (roles && !roles.includes(user.role)) return <Navigate to="/" replace />;
  return children;
}

export default function App() {
  return (
    <Suspense fallback={<PageLoader />}>
      <Routes>
        <Route path="/driver" element={<Guard roles={["Driver"]}><DriverPage /></Guard>} />
        <Route path="/admin" element={<Guard roles={["Admin"]}><AdminShell /></Guard>}>
          <Route index element={<OverviewPage />} />
          <Route path="operators" element={<OperatorsPage />} />
          <Route path="bulk-upload" element={<BulkUploadPage />} />
          <Route path="buses" element={<BusesPage />} />
          <Route path="drivers" element={<DriversPage />} />
          <Route path="stops" element={<StopsPage />} />
          <Route path="routes" element={<RoutesPage />} />
          <Route path="schedules" element={<SchedulesPage />} />
          <Route path="live" element={<LivePage />} />
          <Route path="reports" element={<ReportsPage />} />
          <Route path="workflows" element={<WorkflowsPage />} />
        </Route>
        <Route element={<Shell><ErrorBoundary><Outlet /></ErrorBoundary></Shell>}>
          <Route index element={<HomePage />} />
          <Route path="assistant" element={<AiAssistantPage />} />
          <Route path="ai-assistant" element={<AiAssistantPage />} />
          <Route path="routes" element={<LiveRoutesPage />} />
          <Route path="enquiry-numbers" element={<EnquiryNumbersPage />} />
          <Route path="search" element={<SearchPage />} />
          <Route path="stop/:code" element={<StopTimetablePage />} />
          <Route path="stand/:code" element={<StopTimetablePage />} />
          <Route path="qr/:code" element={<StopTimetablePage />} />
          <Route path="bus/:fleet" element={<BusPage />} />
          <Route path="track/:fleet" element={<TrackPage />} />
          <Route path="login" element={<LoginPage />} />
          <Route path="register" element={<RegisterPage />} />
          <Route path="favorites" element={<Guard><FavoritesPage /></Guard>} />
          <Route path="notifications" element={<Guard><NotificationsPage /></Guard>} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Route>
      </Routes>
    </Suspense>
  );
}
