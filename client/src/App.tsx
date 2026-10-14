import { Navigate, Outlet, Route, Routes, useLocation } from "react-router-dom";
import type { ReactNode } from "react";
import { Shell } from "@/components/Shell";
import { useAuth } from "@/lib/auth";
import { HomePage } from "@/pages/Home";
import { SearchPage } from "@/pages/Search";
import { BusPage } from "@/pages/Bus";
import { TrackPage } from "@/pages/Track";
import { LoginPage } from "@/pages/Login";
import { RegisterPage } from "@/pages/Register";
import { FavoritesPage } from "@/pages/Favorites";
import { NotificationsPage } from "@/pages/Notifications";
import { DriverPage } from "@/pages/Driver";
import { AdminShell } from "@/pages/admin/AdminShell";
import { OverviewPage } from "@/pages/admin/Overview";
import { BusesPage, DriversPage, LivePage, OperatorsPage, ReportsPage, RoutesPage, SchedulesPage, StopsPage } from "@/pages/admin/DataPages";

function Guard({ roles, children }: { roles?: string[]; children: ReactNode }) {
  const { user, loading } = useAuth();
  const location = useLocation();
  if (loading) return <p className="p-8 text-muted-foreground">Checking your session…</p>;
  if (!user) return <Navigate to={`/login?next=${encodeURIComponent(location.pathname)}`} replace />;
  if (roles && !roles.includes(user.role)) return <Navigate to="/" replace />;
  return children;
}

export default function App() {
  return (
    <Routes>
      <Route path="/driver" element={<Guard roles={["Driver"]}><DriverPage /></Guard>} />
      <Route path="/admin" element={<Guard roles={["Admin"]}><AdminShell /></Guard>}>
        <Route index element={<OverviewPage />} />
        <Route path="operators" element={<OperatorsPage />} />
        <Route path="buses" element={<BusesPage />} />
        <Route path="drivers" element={<DriversPage />} />
        <Route path="stops" element={<StopsPage />} />
        <Route path="routes" element={<RoutesPage />} />
        <Route path="schedules" element={<SchedulesPage />} />
        <Route path="live" element={<LivePage />} />
        <Route path="reports" element={<ReportsPage />} />
      </Route>
      <Route element={<Shell><Outlet /></Shell>}>
        <Route index element={<HomePage />} />
        <Route path="search" element={<SearchPage />} />
        <Route path="bus/:fleet" element={<BusPage />} />
        <Route path="track/:fleet" element={<TrackPage />} />
        <Route path="login" element={<LoginPage />} />
        <Route path="register" element={<RegisterPage />} />
        <Route path="favorites" element={<Guard><FavoritesPage /></Guard>} />
        <Route path="notifications" element={<Guard><NotificationsPage /></Guard>} />
        <Route path="*" element={<Navigate to="/" replace />} />
      </Route>
    </Routes>
  );
}
