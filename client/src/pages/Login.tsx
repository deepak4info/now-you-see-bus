import { useState, type FormEvent } from "react";
import { Link, useNavigate, useSearchParams } from "react-router-dom";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { useAuth } from "@/lib/auth";

const modes = [
  { id: "passenger", title: "Passenger", hint: "Email you registered with", sample: "meera@whereismybus.local" },
  { id: "driver", title: "Driver", hint: "Driver ID from dispatch", sample: "DRV-RAHUL" },
  { id: "admin", title: "Dispatch", hint: "Admin email", sample: "admin@whereismybus.local" },
] as const;

export function LoginPage() {
  const [mode, setMode] = useState<(typeof modes)[number]["id"]>("passenger");
  const [login, setLogin] = useState("meera@whereismybus.local");
  const [password, setPassword] = useState("Passenger@12345");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const auth = useAuth();
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const current = modes.find((m) => m.id === mode)!;

  function pick(next: (typeof modes)[number]["id"]) {
    setMode(next);
    setError("");
    if (next === "driver") {
      setLogin("DRV-RAHUL");
      setPassword("Driver@12345");
    } else if (next === "admin") {
      setLogin("admin@whereismybus.local");
      setPassword("Admin@12345");
    } else {
      setLogin("meera@whereismybus.local");
      setPassword("Passenger@12345");
    }
  }

  async function submit(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError("");
    try {
      const user = await auth.login(login, password);
      const next = params.get("next");
      if (user.role === "Admin") navigate("/admin");
      else if (user.role === "Driver") navigate("/driver");
      else if (next && next.startsWith("/") && !next.startsWith("//")) navigate(next);
      else navigate("/");
    } catch (err) {
      setError(err instanceof Error ? err.message : "Sign in failed");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="mx-auto grid max-w-lg px-4 py-12">
      <h1 className="font-display text-4xl">Sign in</h1>
      <p className="mt-2 text-muted-foreground">Passengers can search without an account. Drivers and dispatch need one.</p>
      <div className="mt-6 grid grid-cols-3 gap-2">
        {modes.map((item) => (
          <button
            key={item.id}
            type="button"
            onClick={() => pick(item.id)}
            className={`rounded-lg border px-2 py-2 text-sm ${mode === item.id ? "border-pine bg-pine text-white" : "bg-card"}`}
          >
            {item.title}
          </button>
        ))}
      </div>
      <form onSubmit={submit} className="mt-5 grid gap-4 rounded-2xl border bg-card p-5">
        <div className="grid gap-1.5">
          <Label htmlFor="login">{mode === "driver" ? "Driver ID" : "Email"}</Label>
          <Input id="login" value={login} onChange={(e) => setLogin(e.target.value)} autoComplete="username" />
          <p className="text-xs text-muted-foreground">{current.hint}. Sample: {current.sample}</p>
        </div>
        <div className="grid gap-1.5">
          <Label htmlFor="password">Password</Label>
          <Input id="password" type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="current-password" />
        </div>
        {error && <p className="text-sm text-destructive">{error}</p>}
        <Button type="submit" disabled={busy}>{busy ? "Checking…" : "Sign in"}</Button>
      </form>
      <p className="mt-4 text-sm text-muted-foreground">
        New passenger? <Link className="text-pine underline" to="/register">Create an account</Link>
      </p>
    </div>
  );
}
