import { useCallback, useEffect, useState } from "react";

const TOKEN_KEY = "wimb_token";

function normalizeBaseUrl(url?: string): string {
  if (!url) return "";
  return url.replace(/\/swagger(?:\/.*)?$/i, "").replace(/\/+$/, "");
}

export const API_BASE_URL = normalizeBaseUrl(
  (typeof import.meta !== "undefined" && import.meta.env?.VITE_API_BASE_URL) ||
  "https://fatehabad4info-001-site1.itempurl.com"
);

export function resolveUrl(path: string): string {
  if (!path) return "";
  if (path.startsWith("http://") || path.startsWith("https://")) {
    return path;
  }
  const cleanPath = path.startsWith("/") ? path : `/${path}`;
  if (typeof window !== "undefined") {
    try {
      const targetHost = new URL(API_BASE_URL).host;
      if (window.location.host === targetHost) {
        return cleanPath;
      }
    } catch {
      if (window.location.origin === API_BASE_URL) {
        return cleanPath;
      }
    }
  }
  return `${API_BASE_URL}${cleanPath}`;
}

export function getToken() {
  return localStorage.getItem(TOKEN_KEY);
}

export function setToken(token: string | null) {
  if (token) localStorage.setItem(TOKEN_KEY, token);
  else localStorage.removeItem(TOKEN_KEY);
}

export class ApiError extends Error {
  status: number;
  constructor(message: string, status: number) {
    super(message);
    this.status = status;
  }
}

export async function api<T>(path: string, options: RequestInit = {}): Promise<T> {
  const token = getToken();
  const headers = new Headers(options.headers);
  if (options.body && !headers.has("Content-Type")) headers.set("Content-Type", "application/json");
  if (token) headers.set("Authorization", `Bearer ${token}`);
  const url = resolveUrl(path);
  const res = await fetch(url, { ...options, headers });
  if (res.status === 204) return undefined as T;
  const contentType = res.headers.get("content-type") ?? "";
  const text = await res.text();
  const data = text ? safeJson(text) : null;
  if (!res.ok) {
    const message = (data && typeof data === "object" && "message" in data && typeof data.message === "string")
      ? data.message
      : res.statusText || "Request failed";
    if (res.status === 401 && token && !path.includes("/api/auth/login")) {
      setToken(null);
    }
    throw new ApiError(message, res.status);
  }
  if (text.trim().length > 0 && data === null) {
    throw new ApiError(
      contentType.includes("text/html")
        ? "This service endpoint is not available yet on the backend server."
        : "Server returned an unexpected response format.",
      res.status,
    );
  }
  return data as T;
}

function safeJson(text: string) {
  try { return JSON.parse(text); } catch { return null; }
}

// In-memory client cache for static reference data (e.g. stops list)
const clientApiCache = new Map<string, { data: unknown; timestamp: number }>();
const DEFAULT_CLIENT_CACHE_TTL_MS = 60_000; // 1 minute

export function useQuery<T>(path: string | null, options?: { cacheTtlMs?: number }) {
  const cacheTtl = options?.cacheTtlMs ?? (path === "/api/stops" ? DEFAULT_CLIENT_CACHE_TTL_MS : 0);

  const [data, setData] = useState<T | null>(() => {
    if (!path || cacheTtl <= 0) return null;
    const hit = clientApiCache.get(path);
    if (hit && Date.now() - hit.timestamp < cacheTtl) {
      return hit.data as T;
    }
    return null;
  });
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(data === null && Boolean(path));

  const reload = useCallback(async () => {
    if (!path) return;
    setLoading(true);
    setError(null);
    try {
      const result = await api<T>(path);
      if (cacheTtl > 0) {
        clientApiCache.set(path, { data: result, timestamp: Date.now() });
      }
      setData(result);
    } catch (err) {
      if (err instanceof Error && err.name === "AbortError") return;
      setError(err instanceof Error ? err.message : "Request failed");
    } finally {
      setLoading(false);
    }
  }, [path, cacheTtl]);

  useEffect(() => {
    if (!path) {
      setData(null);
      setError(null);
      setLoading(false);
      return;
    }

    if (cacheTtl > 0) {
      const hit = clientApiCache.get(path);
      if (hit && Date.now() - hit.timestamp < cacheTtl) {
        setData(hit.data as T);
        setLoading(false);
        return;
      }
    }

    const controller = new AbortController();
    setLoading(true);
    setError(null);

    api<T>(path, { signal: controller.signal })
      .then((res) => {
        if (cacheTtl > 0) {
          clientApiCache.set(path, { data: res, timestamp: Date.now() });
        }
        setData(res);
      })
      .catch((err) => {
        if (err instanceof Error && err.name === "AbortError") return;
        setError(err instanceof Error ? err.message : "Request failed");
      })
      .finally(() => {
        setLoading(false);
      });

    return () => {
      controller.abort();
    };
  }, [path, cacheTtl]);

  return { data, error, loading, reload, setData };
}
