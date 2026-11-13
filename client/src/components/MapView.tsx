import { useEffect, useRef, useState } from "react";
import L from "leaflet";
import "leaflet/dist/leaflet.css";

export type StopPin = { name: string; latitude: number; longitude: number; status?: string };
export type BusPin = {
  latitude: number;
  longitude: number;
  heading?: number | null;
  label: string;
  variant?: "live" | "last-known" | "stale";
  recordedAt?: string | null;
  fleetNumber?: string;
  routeName?: string;
  routeCode?: string;
  speedKmph?: number | null;
  status?: string;
};

// In-memory cache for client-side resolved road geometries across component renders
const roadCache = new Map<string, { latitude: number; longitude: number }[]>();

async function fetchRoadGeometry(stops: StopPin[]): Promise<{ latitude: number; longitude: number }[] | null> {
  if (stops.length < 2) return null;
  const key = stops.map((s) => `${s.latitude.toFixed(5)},${s.longitude.toFixed(5)}`).join(";");
  if (roadCache.has(key)) return roadCache.get(key)!;

  const coords = stops.map((s) => `${s.longitude.toFixed(6)},${s.latitude.toFixed(6)}`).join(";");
  const urls = [
    `https://router.project-osrm.org/route/v1/driving/${coords}?overview=full&geometries=geojson`,
    `https://routing.openstreetmap.de/routed-car/route/v1/driving/${coords}?overview=full&geometries=geojson`,
  ];

  for (const url of urls) {
    try {
      const res = await fetch(url);
      if (!res.ok) continue;
      const data = await res.json();
      if (data.code === "Ok" && data.routes?.[0]?.geometry?.coordinates?.length) {
        const coordsList: [number, number][] = data.routes[0].geometry.coordinates;
        const pts = coordsList.map(([lng, lat]) => ({ latitude: lat, longitude: lng }));
        if (pts.length >= 2) {
          roadCache.set(key, pts);
          return pts;
        }
      }
    } catch {
      // try next url
    }
  }

  // Fallback: route leg-by-leg so intermediate hops snap to road
  try {
    const combinedPts: { latitude: number; longitude: number }[] = [];
    for (let i = 0; i < stops.length - 1; i++) {
      const a = stops[i];
      const b = stops[i + 1];
      const legCoords = `${a.longitude.toFixed(6)},${a.latitude.toFixed(6)};${b.longitude.toFixed(6)},${b.latitude.toFixed(6)}`;
      let legFound = false;
      for (const base of ["https://router.project-osrm.org", "https://routing.openstreetmap.de/routed-car"]) {
        try {
          const res = await fetch(`${base}/route/v1/driving/${legCoords}?overview=full&geometries=geojson`);
          if (!res.ok) continue;
          const data = await res.json();
          if (data.code === "Ok" && data.routes?.[0]?.geometry?.coordinates?.length) {
            const raw: [number, number][] = data.routes[0].geometry.coordinates;
            const startIdx = combinedPts.length > 0 ? 1 : 0;
            for (let j = startIdx; j < raw.length; j++) {
              combinedPts.push({ latitude: raw[j][1], longitude: raw[j][0] });
            }
            legFound = true;
            break;
          }
        } catch {}
      }
      if (!legFound) {
        if (combinedPts.length === 0) combinedPts.push({ latitude: a.latitude, longitude: a.longitude });
        combinedPts.push({ latitude: b.latitude, longitude: b.longitude });
      }
    }
    if (combinedPts.length >= 2) {
      roadCache.set(key, combinedPts);
      return combinedPts;
    }
  } catch {}

  return null;
}

export function MapView({
  path = [],
  stops = [],
  bus,
  buses,
  focusBus = false,
  className,
  onBusClick,
}: {
  path?: { latitude: number; longitude: number }[];
  stops?: StopPin[];
  bus?: BusPin | null;
  buses?: BusPin[];
  focusBus?: boolean;
  className?: string;
  onBusClick?: (bus: BusPin) => void;
}) {
  const el = useRef<HTMLDivElement>(null);
  const mapRef = useRef<L.Map | null>(null);
  const layerRef = useRef<L.LayerGroup | null>(null);
  const fitted = useRef("");
  const [resolvedRoad, setResolvedRoad] = useState<{ latitude: number; longitude: number }[] | null>(null);

  const safePath = path ?? [];
  const safeStops = stops ?? [];

  // If backend path is sparse or straight-line densified, resolve road geometry via highway routing
  useEffect(() => {
    let active = true;
    const isSparse = safePath.length <= Math.max(safeStops.length * 3, 25);
    if (isSparse && safeStops.length >= 2) {
      fetchRoadGeometry(safeStops).then((road) => {
        if (active && road && road.length > 1) {
          setResolvedRoad(road);
        }
      });
    } else {
      setResolvedRoad(null);
    }
    return () => {
      active = false;
    };
  }, [safePath, safeStops]);

  const effectivePath = resolvedRoad && resolvedRoad.length > 1 ? resolvedRoad : safePath;
  const activeBuses: BusPin[] = (buses && buses.length > 0 ? buses : (bus ? [bus] : [])).filter(
    (b) => b && typeof b.latitude === "number" && !isNaN(b.latitude) && typeof b.longitude === "number" && !isNaN(b.longitude),
  );

  useEffect(() => {
    if (!el.current || mapRef.current) return;
    try {
      if ((el.current as unknown as { _leaflet_id?: number })._leaflet_id != null) {
        delete (el.current as unknown as { _leaflet_id?: number })._leaflet_id;
      }
      const map = L.map(el.current, { zoomControl: false });
      L.control.zoom({ position: "bottomright" }).addTo(map);
      L.tileLayer("https://tile.openstreetmap.org/{z}/{x}/{y}.png", {
        attribution: "&copy; OpenStreetMap",
        maxZoom: 19,
      }).addTo(map);
      map.setView([29.45, 75.72], 8);
      layerRef.current = L.layerGroup().addTo(map);
      mapRef.current = map;
      let alive = true;
      const timer = window.setTimeout(() => {
        if (alive) map.invalidateSize();
      }, 200);
      return () => {
        alive = false;
        window.clearTimeout(timer);
        map.remove();
        mapRef.current = null;
        layerRef.current = null;
      };
    } catch (err) {
      console.warn("Leaflet map initialization skipped or caught:", err);
    }
  }, []);

  useEffect(() => {
    const map = mapRef.current;
    const layer = layerRef.current;
    if (!map || !layer) return;

    try {
      layer.clearLayers();

      const validPath = effectivePath.filter(
        (p) => p && typeof p.latitude === "number" && !isNaN(p.latitude) && typeof p.longitude === "number" && !isNaN(p.longitude),
      );
      const validStops = safeStops.filter(
        (s) => s && typeof s.latitude === "number" && !isNaN(s.latitude) && typeof s.longitude === "number" && !isNaN(s.longitude),
      );

      // 1. Draw Highway Road Polyline
      if (validPath.length > 1) {
        L.polyline(
          validPath.map((p) => [p.latitude, p.longitude] as [number, number]),
          { color: "#1d6b4f", weight: 6, opacity: 0.95 },
        ).addTo(layer);
      } else if (validStops.length > 1) {
        L.polyline(
          validStops.map((s) => [s.latitude, s.longitude] as [number, number]),
          { color: "#1d6b4f", weight: 4, opacity: 0.8, dashArray: "6, 8" },
        ).addTo(layer);
      }

      // 2. Render Station Stops
      validStops.forEach((stop, index) => {
        const isOrigin = index === 0;
        const isDestination = index === validStops.length - 1;
        const fill = stop.status === "Current"
          ? "#c4512c"
          : stop.status === "Passed"
          ? "#1d6b4f"
          : isOrigin
          ? "#16a34a"
          : isDestination
          ? "#dc2626"
          : "#1b1714";

        const radius = isOrigin || isDestination ? 8 : 6;
        const weight = isOrigin || isDestination ? 3 : 2;

        const marker = L.circleMarker([stop.latitude, stop.longitude], {
          radius,
          color: "#ffffff",
          weight,
          fillColor: fill,
          fillOpacity: 1,
        }).bindTooltip(
          `${isOrigin ? "Origin: " : isDestination ? "Destination: " : ""}${stop.name}`,
          { direction: "top", offset: [0, -6] }
        ).addTo(layer);

        const statusBadge = stop.status ? `<div style="font-size:11px;color:#64748b;margin-top:2px;">Status: <b>${stop.status}</b></div>` : "";
        marker.bindPopup(`
          <div style="font-family: inherit; font-size: 13px; line-height: 1.4;">
            <div style="font-weight: 700;">${stop.name}</div>
            <div style="font-size: 11px; color: #64748b;">Stop #${index + 1} on route</div>
            ${statusBadge}
          </div>
        `);
      });

      // 3. Render Bus Pins
      activeBuses.forEach((b) => {
        const label = b.label.replace(/[&<>"']/g, (character) => ({
          "&": "&amp;",
          "<": "&lt;",
          ">": "&gt;",
          "\"": "&quot;",
          "'": "&#39;",
        })[character] ?? character);

        const isLastKnown = b.variant === "last-known";
        const markerClass = isLastKnown ? "bus-marker bus-marker-last-known" : "bus-marker bus-marker-live";
        const iconEmoji = isLastKnown ? "📡" : "🚌";
        const badgeHtml = isLastKnown
          ? `<div class="bus-marker-pill">Last known</div>`
          : `<div class="bus-marker-live-dot" aria-hidden="true"></div>`;

        const icon = L.divIcon({
          className: "bus-marker-wrap",
          html: `<div class="${markerClass}"><span aria-hidden="true">${iconEmoji}</span><b>${label}</b>${badgeHtml}</div>`,
          iconSize: [46, 46],
          iconAnchor: [23, 23],
        });

        const marker = L.marker([b.latitude, b.longitude], { icon, zIndexOffset: 900 }).addTo(layer);

        const fleetTarget = b.fleetNumber || label;
        const popupHtml = `
          <div style="font-family: inherit; font-size: 13px; line-height: 1.4; min-width: 150px; padding: 2px;">
            <div style="font-weight: 700; font-size: 15px;">Bus ${label}</div>
            ${b.routeName ? `<div style="color: #64748b; font-size: 11px; margin-bottom: 4px;">${b.routeName}</div>` : ""}
            <div style="display: flex; align-items: center; gap: 5px; margin: 4px 0;">
              <span style="display: inline-block; width: 8px; height: 8px; border-radius: 50%; background: ${isLastKnown ? "#f59e0b" : "#10b981"};"></span>
              <span style="font-size: 11px; font-weight: 600;">${isLastKnown ? "Last known location" : "Live on road"}</span>
              ${b.speedKmph != null && b.speedKmph > 0 ? `<span style="font-size: 11px; color: #64748b;">· ${Math.round(b.speedKmph)} km/h</span>` : ""}
            </div>
            <div style="margin-top: 8px; border-top: 1px solid #e2e8f0; padding-top: 6px;">
              <a href="/track/${encodeURIComponent(fleetTarget)}" style="font-size: 12px; color: #1d6b4f; font-weight: 600; text-decoration: underline;">
                Track this bus live →
              </a>
            </div>
          </div>
        `;
        marker.bindPopup(popupHtml);

        if (onBusClick) {
          marker.on("click", () => onBusClick(b));
        }
      });

      const token = validStops.map((s) => s.name).join("|") + validPath.length + activeBuses.map((b) => `${b.label}-${b.latitude}-${b.longitude}`).join(";");
      if (focusBus && activeBuses.length > 0) {
        map.setView([activeBuses[0].latitude, activeBuses[0].longitude], 13, { animate: false });
        fitted.current = `bus:${token}`;
      } else if (fitted.current !== token) {
        const bounds: [number, number][] = [
          ...validPath.map((p) => [p.latitude, p.longitude] as [number, number]),
          ...validStops.map((s) => [s.latitude, s.longitude] as [number, number]),
          ...activeBuses.map((b) => [b.latitude, b.longitude] as [number, number]),
        ];
        if (bounds.length > 0) {
          map.fitBounds(bounds as L.LatLngBoundsExpression, { padding: [28, 28], maxZoom: 12 });
          fitted.current = token;
        }
      }

      let alive = true;
      const timer = window.setTimeout(() => {
        if (alive) map.invalidateSize();
      }, 50);
      return () => {
        alive = false;
        window.clearTimeout(timer);
      };
    } catch (err) {
      console.warn("MapView layer render error handled:", err);
    }
  }, [effectivePath, safeStops, bus, buses, focusBus, onBusClick]);

  return <div ref={el} className={className ?? "h-full min-h-[320px] w-full"} />;
}
