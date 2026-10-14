import { useEffect, useRef } from "react";
import L from "leaflet";
import "leaflet/dist/leaflet.css";

type StopPin = { name: string; latitude: number; longitude: number; status?: string };
type BusPin = { latitude: number; longitude: number; heading?: number | null; label: string };

export function MapView({
  path,
  stops,
  bus,
  className,
}: {
  path: { latitude: number; longitude: number }[];
  stops: StopPin[];
  bus?: BusPin | null;
  className?: string;
}) {
  const el = useRef<HTMLDivElement>(null);
  const mapRef = useRef<L.Map | null>(null);
  const layerRef = useRef<L.LayerGroup | null>(null);
  const fitted = useRef("");

  useEffect(() => {
    if (!el.current || mapRef.current) return;
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
  }, []);

  useEffect(() => {
    const map = mapRef.current;
    const layer = layerRef.current;
    if (!map || !layer) return;
    layer.clearLayers();
    if (path.length > 1) {
      L.polyline(
        path.map((p) => [p.latitude, p.longitude] as [number, number]),
        { color: "#1d6b4f", weight: 5, opacity: 0.9 },
      ).addTo(layer);
    }
    stops.forEach((stop) => {
      const fill = stop.status === "Current" ? "#c4512c" : stop.status === "Passed" ? "#1d6b4f" : "#1b1714";
      L.circleMarker([stop.latitude, stop.longitude], {
        radius: 7,
        color: "#fffaf3",
        weight: 2,
        fillColor: fill,
        fillOpacity: 1,
      }).bindTooltip(stop.name, { direction: "top" }).addTo(layer);
    });
    if (bus) {
      const icon = L.divIcon({
        className: "bus-marker-wrap",
        html: `<div class="bus-marker"><i style="transform: rotate(${bus.heading ?? 0}deg)"></i><b>${bus.label}</b></div>`,
        iconSize: [46, 46],
        iconAnchor: [23, 23],
      });
      L.marker([bus.latitude, bus.longitude], { icon, zIndexOffset: 800 }).addTo(layer);
    }
    const token = stops.map((s) => s.name).join("|") + path.length;
    if (fitted.current !== token) {
      const bounds: L.LatLngExpression[] = [
        ...path.map((p) => [p.latitude, p.longitude] as [number, number]),
        ...stops.map((s) => [s.latitude, s.longitude] as [number, number]),
      ];
      if (bounds.length) {
        map.fitBounds(bounds as L.LatLngBoundsExpression, { padding: [28, 28], maxZoom: 11 });
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
  }, [path, stops, bus]);

  return <div ref={el} className={className ?? "h-full min-h-[320px] w-full"} />;
}
