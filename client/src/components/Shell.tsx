import { Link, NavLink } from "react-router-dom";
import { useEffect, useState, type ReactNode } from "react";
import { Menu, UsersRound, X } from "lucide-react";
import { useAuth } from "@/lib/auth";
import { Button } from "@/components/ui/button";
import { api } from "@/lib/api";
import { cn } from "@/lib/utils";
import { PwaBanner, InstallAppButton } from "./PwaBanner";
import { SoundToggle } from "./SoundToggle";

const VISITOR_ID_KEY = "wimb_visitor_id";

const links = [
  { to: "/", label: "Find a bus", end: true },
  { to: "/routes", label: "Live routes" },
  { to: "/enquiry-numbers", label: "Bus stand numbers" },
  { to: "/favorites", label: "Favourites" },
  { to: "/notifications", label: "Alerts" },
];

export function Shell({ children }: { children: ReactNode }) {
  const { user, logout } = useAuth();
  const [open, setOpen] = useState(false);
  const [visitorCount, setVisitorCount] = useState<number | null>(null);
  const [visitorError, setVisitorError] = useState("");

  useEffect(() => {
    let active = true;
    async function registerVisitor() {
      try {
        const visitorId = getOrCreateVisitorId();
        const result = await api<{ visitorCount: number }>("/api/visitors", {
          method: "POST",
          body: JSON.stringify({ visitorId }),
        });
        if (active) setVisitorCount(result.visitorCount);
      } catch (err) {
        if (active) setVisitorError(err instanceof Error ? err.message : "Could not load visitor count.");
      }
    }
    void registerVisitor();
    return () => { active = false; };
  }, []);

  return (
    <div className="min-h-screen">
      <PwaBanner />
      <header className="sticky top-0 z-[1100] border-b border-border/80 bg-paper/90 backdrop-blur">
        <div className="mx-auto flex h-16 max-w-6xl items-center gap-4 px-4">
          <Link to="/" className="flex items-center gap-2">
            <span className="grid h-9 w-9 place-items-center rounded-xl bg-pine text-lg text-white">🚌</span>
            <span className="font-display text-xl leading-none">Where is my bus</span>
          </Link>
          <InstallAppButton className="hidden sm:inline-flex" />
          <SoundToggle className="hidden sm:grid" />
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
          </nav>
          <div className="ml-auto hidden items-center gap-2 md:flex">
            {user ? (
              <>
                {user.role === "Driver" && (
                  <Button variant="ghost" size="sm" asChild>
                    <Link to="/driver">Driver console</Link>
                  </Button>
                )}
                {user.role === "Admin" && (
                  <Button variant="ghost" size="sm" asChild>
                    <Link to="/admin">Dispatch console</Link>
                  </Button>
                )}
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
            <div className="flex items-center gap-2 pb-2">
              <InstallAppButton className="flex-1 justify-center py-2" />
              <SoundToggle />
            </div>
            {links.map((link) => (
              <Link key={link.to} to={link.to} className="block rounded-md px-2 py-2" onClick={() => setOpen(false)}>
                {link.label}
              </Link>
            ))}
            {user?.role === "Driver" && (
              <Link to="/driver" className="block rounded-md px-2 py-2 font-medium text-pine" onClick={() => setOpen(false)}>
                Driver console
              </Link>
            )}
            {user?.role === "Admin" && (
              <Link to="/admin" className="block rounded-md px-2 py-2 font-medium text-pine" onClick={() => setOpen(false)}>
                Dispatch console
              </Link>
            )}
            {!user && (
              <Link to="/login" className="block rounded-md px-2 py-2 font-medium" onClick={() => setOpen(false)}>
                Sign in
              </Link>
            )}
            {user && <button className="block px-2 py-2 text-left" onClick={() => { logout(); setOpen(false); }}>Sign out</button>}
          </div>
        )}
      </header>
      <main>{children}</main>
      <footer className="mt-10 border-t border-border/80 bg-card/60">
        <div className="mx-auto flex max-w-6xl flex-wrap items-center justify-between gap-4 px-4 py-6">
          <Link to="/" className="font-display text-lg text-foreground">Where is my bus</Link>
          <div className="flex items-center gap-3 rounded-xl border bg-card px-4 py-3">
            <span className="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-primary/10 text-primary">
              <UsersRound size={18} aria-hidden="true" />
            </span>
            <div>
              <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">Website visitors</p>
              <p className="text-sm font-semibold" aria-live="polite">
                {visitorCount == null
                  ? visitorError ? "Count unavailable" : "Counting visitors…"
                  : `${new Intl.NumberFormat().format(visitorCount)} unique ${visitorCount === 1 ? "visitor" : "visitors"}`}
              </p>
              {visitorError && <p className="mt-0.5 text-xs text-destructive">{visitorError}</p>}
            </div>
          </div>
          <p className="w-full text-xs text-muted-foreground sm:w-auto">One count per browser</p>
        </div>
      </footer>
    </div>
  );
}

function getOrCreateVisitorId() {
  const storedId = localStorage.getItem(VISITOR_ID_KEY);
  if (storedId && /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(storedId)) {
    return storedId;
  }
  if (typeof crypto.randomUUID !== "function") {
    throw new Error("This browser cannot create an anonymous visitor ID.");
  }
  const visitorId = crypto.randomUUID();
  localStorage.setItem(VISITOR_ID_KEY, visitorId);
  return visitorId;
}
