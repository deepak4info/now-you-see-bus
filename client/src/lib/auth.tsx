import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { api, getToken, setToken } from "@/lib/api";
import type { AuthUser } from "@/lib/types";

type AuthState = {
  user: AuthUser | null;
  loading: boolean;
  login: (login: string, password: string) => Promise<AuthUser>;
  register: (input: { fullName: string; email: string; phone?: string; password: string }) => Promise<AuthUser>;
  logout: () => void;
};

const AuthContext = createContext<AuthState | null>(null);

type AuthPayload = AuthUser & { token: string };

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<AuthUser | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    const token = getToken();
    if (!token) {
      setLoading(false);
      return;
    }
    api<AuthUser>("/api/auth/me")
      .then(setUser)
      .catch(() => setToken(null))
      .finally(() => setLoading(false));
  }, []);

  const value = useMemo<AuthState>(() => ({
    user,
    loading,
    async login(login, password) {
      const result = await api<AuthPayload>("/api/auth/login", {
        method: "POST",
        body: JSON.stringify({ login, password }),
      });
      setToken(result.token);
      const next = { fullName: result.fullName, email: result.email, role: result.role, driverCode: result.driverCode };
      setUser(next);
      return next;
    },
    async register(input) {
      const result = await api<AuthPayload>("/api/auth/register", {
        method: "POST",
        body: JSON.stringify(input),
      });
      setToken(result.token);
      const next = { fullName: result.fullName, email: result.email, role: result.role, driverCode: result.driverCode };
      setUser(next);
      return next;
    },
    logout() {
      setToken(null);
      setUser(null);
    },
  }), [user, loading]);

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("AuthProvider is missing");
  return ctx;
}
