import { lazy, Suspense } from "react";
import { Navigate, Outlet, Route, Routes, useLocation } from "react-router-dom";
import type { ReactNode } from "react";
import { ErrorBoundary } from "@/components/ErrorBoundary";
import { Shell } from "@/components/Shell";
import { useAuth } from "@/lib/auth";
import { AiAssistantPage } from "@/pages/AiAssistantPage";

function lazyWithRetry<T extends { [key: string]: any }>(
  factory: () => Promise<T>,
  componentName: keyof T = "default"
) {
  return lazy(async () => {
    try {
      const module = await factory();
      const component = (module[componentName] ?? module.default) as React.ComponentType<any>;
      return { default: component };
    } catch (err) {
      console.warn("Chunk load error, retrying page reload:", err);
      const reloadedKey = "wimb_last_chunk_reload";
      const lastReload = Number(sessionStorage.getItem(reloadedKey) || "0");
      if (Date.now() - lastReload > 10000) {
        sessionStorage.setItem(reloadedKey, Date.now().toString());
        window.location.reload();
      }
      throw err;
    }
  });
}

// Public & Passenger Pages (Lazy Loaded for minimal initial bundle)
const HomePage = lazyWithRetry(() => import("@/pages/Home"), "HomePage");
const LiveRoutesPage = lazyWithRetry(() => import("@/pages/LiveRoutes"), "LiveRoutesPage");
const EnquiryNumbersPage = lazyWithRetry(() => import("@/pages/EnquiryNumbers"), "EnquiryNumbersPage");
const SearchPage = lazyWithRetry(() => import("@/pages/Search"), "SearchPage");
const BusPage = lazyWithRetry(() => import("@/pages/Bus"), "BusPage");
const TrackPage = lazyWithRetry(() => import("@/pages/Track"), "TrackPage");
const LoginPage = lazyWithRetry(() => import("@/pages/Login"), "LoginPage");
const RegisterPage = lazyWithRetry(() => import("@/pages/Register"), "RegisterPage");
const FavoritesPage = lazyWithRetry(() => import("@/pages/Favorites"), "FavoritesPage");
const NotificationsPage = lazyWithRetry(() => import("@/pages/Notifications"), "NotificationsPage");
const DriverPage = lazyWithRetry(() => import("@/pages/Driver"), "DriverPage");
const StopTimetablePage = lazyWithRetry(() => import("@/pages/StopTimetable"), "StopTimetablePage");

// Admin Management Suite (Code-Split into separate isolated chunk)
const AdminShell = lazyWithRetry(() => import("@/pages/admin/AdminShell"), "AdminShell");
const OverviewPage = lazyWithRetry(() => import("@/pages/admin/Overview"), "OverviewPage");
const BulkUploadPage = lazyWithRetry(() => import("@/pages/admin/BulkUpload"), "BulkUploadPage");
const WorkflowsPage = lazyWithRetry(() => import("@/pages/admin/Workflows"), "WorkflowsPage");
const OperatorsPage = lazyWithRetry(() => import("@/pages/admin/DataPages"), "OperatorsPage");
const BusesPage = lazyWithRetry(() => import("@/pages/admin/DataPages"), "BusesPage");
const DriversPage = lazyWithRetry(() => import("@/pages/admin/DataPages"), "DriversPage");
const StopsPage = lazyWithRetry(() => import("@/pages/admin/DataPages"), "StopsPage");
const RoutesPage = lazyWithRetry(() => import("@/pages/admin/DataPages"), "RoutesPage");
const SchedulesPage = lazyWithRetry(() => import("@/pages/admin/DataPages"), "SchedulesPage");
const LivePage = lazyWithRetry(() => import("@/pages/admin/DataPages"), "LivePage");
const ReportsPage = lazyWithRetry(() => import("@/pages/admin/DataPages"), "ReportsPage");

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
    <ErrorBoundary>
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
    </ErrorBoundary>
  );
}
