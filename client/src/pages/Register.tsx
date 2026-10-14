import { useState, type FormEvent } from "react";
import { Link, useNavigate } from "react-router-dom";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { useAuth } from "@/lib/auth";

export function RegisterPage() {
  const [fullName, setFullName] = useState("");
  const [email, setEmail] = useState("");
  const [phone, setPhone] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const auth = useAuth();
  const navigate = useNavigate();

  async function submit(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError("");
    try {
      await auth.register({ fullName, email, phone, password });
      navigate("/favorites");
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not create the account");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="mx-auto max-w-lg px-4 py-12">
      <h1 className="font-display text-4xl">Create a passenger account</h1>
      <p className="mt-2 text-muted-foreground">Use it to save a bus and receive start and arrival alerts. Driver and admin accounts are issued by dispatch.</p>
      <form onSubmit={submit} className="mt-6 grid gap-4 rounded-2xl border bg-card p-5">
        <Field id="name" label="Name" value={fullName} onChange={setFullName} />
        <Field id="email" label="Email" value={email} onChange={setEmail} type="email" />
        <Field id="phone" label="Phone" value={phone} onChange={setPhone} />
        <Field id="password" label="Password" value={password} onChange={setPassword} type="password" />
        {error && <p className="text-sm text-destructive">{error}</p>}
        <Button type="submit" disabled={busy}>{busy ? "Creating…" : "Create account"}</Button>
      </form>
      <p className="mt-4 text-sm"><Link className="text-pine underline" to="/login">I already have an account</Link></p>
    </div>
  );
}

function Field({ id, label, value, onChange, type = "text" }: { id: string; label: string; value: string; onChange: (value: string) => void; type?: string }) {
  return (
    <div className="grid gap-1.5">
      <Label htmlFor={id}>{label}</Label>
      <Input id={id} type={type} value={value} onChange={(e) => onChange(e.target.value)} required={id !== "phone"} />
    </div>
  );
}
