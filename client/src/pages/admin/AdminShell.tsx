import { NavLink, Outlet } from "react-router-dom";
import { cn } from "@/lib/utils";
import { useAuth } from "@/lib/auth";
import { Button } from "@/components/ui/button";

const items = [
  ["", "Overview"],
  ["operators", "Operators"],
  ["bulk-upload", "Bulk upload"],
    ["drivers", "Drivers"],
  ["buses", "Buses"],
  ["stops", "Stops"],
  ["routes", "Routes"],
  ["schedules", "Schedules"],
  ["live", "Live board"],
  ["reports", "Reports"],
] as const;

export function AdminShell() {
  const { user, logout } = useAuth();
  return (
    <div className="min-h-screen md:grid md:grid-cols-[220px_minmax(0,1fr)]">
      <aside className="border-b bg-ink text-paper md:min-h-screen md:border-b-0 md:border-r">
        <div className="px-4 py-4">
          <p className="text-xs uppercase tracking-[0.18em] text-gold">Dispatch</p>
          <p className="font-display text-2xl">Control room</p>
          <p className="text-xs text-white/60">{user?.fullName}</p>
        </div>
        <nav className="flex gap-1 overflow-x-auto px-2 pb-3 md:block md:space-y-1 md:px-2">
          {items.map(([path, label]) => (
            <NavLink
              key={path}
              to={path === "" ? "/admin" : `/admin/${path}`}
              end={path === ""}
              className={({ isActive }) => cn("block whitespace-nowrap rounded-lg px-3 py-2 text-sm", isActive ? "bg-white text-ink" : "text-white/75 hover:bg-white/10")}
            >
              {label}
            </NavLink>
          ))}
        </nav>
        <div className="hidden px-3 py-4 md:block">
          <Button variant="outline" className="w-full border-white/20 bg-transparent text-paper hover:bg-white/10" onClick={logout}>Sign out</Button>
        </div>
      </aside>
      <div className="min-w-0 px-4 py-6 md:px-8">
        <Outlet />
      </div>
    </div>
  );
}
