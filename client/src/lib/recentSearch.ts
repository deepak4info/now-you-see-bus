const RECENT_SEARCH_KEY = "wimb_recent_search";

export type RecentSearch = {
  from: string;
  to: string;
};

export function getRecentSearch(): RecentSearch | null {
  const stored = localStorage.getItem(RECENT_SEARCH_KEY);
  if (!stored) return null;

  try {
    const value: unknown = JSON.parse(stored);
    if (
      value
      && typeof value === "object"
      && "from" in value
      && "to" in value
      && typeof value.from === "string"
      && typeof value.to === "string"
      && value.from.trim()
      && value.to.trim()
    ) {
      return { from: value.from, to: value.to };
    }
  } catch {
    return null;
  }

  return null;
}

export function saveRecentSearch(from: string, to: string) {
  localStorage.setItem(RECENT_SEARCH_KEY, JSON.stringify({ from, to }));
}
