import { Link, NavLink } from "react-router-dom";
import { useState, type ReactNode } from "react";
import { Menu, X } from "lucide-react";
import { useAuth } from "@/lib/auth";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";

const links = [
  { to: "/", label: "Find a bus", end: true },
  { to: "/favorites", label: "Favourites" },
  { to: "/notifications", label: "Alerts" },
];

export function Shell({ children }: { children: ReactNode }) {
  const { user, logout } = useAuth();
  const [open, setOpen] = useState(false);

  return (
    <div className="min-h-screen">
      <header className="sticky top-0 z-40 border-b border-border/80 bg-paper/90 backdrop-blur">
        <div className="mx-auto flex h-16 max-w-6xl items-center gap-4 px-4">
          <Link to="/" className="flex items-center gap-2">
            <span className="grid h-9 w-9 place-items-center rounded-xl bg-pine text-lg text-white">🚌</span>
            <span className="font-display text-xl leading-none">Where is my bus</span>
          </Link>
          <nav className="ml-auto hidden items-center gap-1 md:flex">
            {links.map((link) => (
              <NavLink
                key={link.to}
                to={link.to}
                end={link.end}
                className={({ isActive }) => cn("rounded-md px-3 py-2 text-sm", isActive ? "bg-white text-ink" : "text-muted-foreground hover:text-ink")}
              >
                {link.label}
              </NavLink>
            ))}
            <NavLink to="/driver" className={({ isActive }) => cn("rounded-md px-3 py-2 text-sm", isActive ? "bg-white" : "text-muted-foreground")}>Driver</NavLink>
            <NavLink to="/admin" className={({ isActive }) => cn("rounded-md px-3 py-2 text-sm", isActive ? "bg-white" : "text-muted-foreground")}>Dispatch</NavLink>
          </nav>
          <div className="ml-auto hidden items-center gap-2 md:flex">
            {user ? (
              <>
                <span className="text-sm text-muted-foreground">{user.fullName}</span>
                <Button variant="outline" size="sm" onClick={logout}>Sign out</Button>
              </>
            ) : (
              <>
                <Button variant="ghost" size="sm" asChild><Link to="/login">Sign in</Link></Button>
                <Button size="sm" asChild><Link to="/register">Create account</Link></Button>
              </>
            )}
          </div>
          <button className="ml-auto md:hidden" onClick={() => setOpen((v) => !v)} aria-label="Menu">
            {open ? <X /> : <Menu />}
          </button>
        </div>
        {open && (
          <div className="space-y-1 border-t px-4 py-3 md:hidden">
            {[...links, { to: "/driver", label: "Driver" }, { to: "/admin", label: "Dispatch" }, { to: "/login", label: "Sign in" }].map((link) => (
              <Link key={link.to} to={link.to} className="block rounded-md px-2 py-2" onClick={() => setOpen(false)}>
                {link.label}
              </Link>
            ))}
            {user && <button className="block px-2 py-2 text-left" onClick={() => { logout(); setOpen(false); }}>Sign out</button>}
          </div>
        )}
      </header>
      <main>{children}</main>
    </div>
  );
}
