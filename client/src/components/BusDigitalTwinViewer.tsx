import { useEffect, useRef, useState } from "react";
import * as THREE from "three";
import {
  Activity,
  Compass,
  Cpu,
  Gauge,
  Info,
  Maximize2,
  Navigation,
  Radio,
  RotateCcw,
  Sparkles,
  Users,
  Volume2,
  Wifi,
} from "lucide-react";
import { transitVoice } from "@/lib/voice";
import { cn } from "@/lib/utils";

export type SubsystemId = "all" | "gps" | "cabin" | "driver" | "engine" | "suspension" | "display";

interface SubsystemInfo {
  id: SubsystemId;
  name: string;
  hindiName: string;
  tagline: string;
  status: "Optimal" | "Active" | "Normal" | "Synchronized";
  metrics: { label: string; value: string; unit?: string }[];
  explanation: {
    en: string;
    hi: string;
  };
  cameraTarget: { x: number; y: number; z: number; distance: number };
}

const SUBSYSTEMS: Record<SubsystemId, SubsystemInfo> = {
  all: {
    id: "all",
    name: "Fleet Digital Twin",
    hindiName: "संपूर्ण 3D बस मॉडल",
    tagline: "Haryana Roadways Express · HR-57-A-1002",
    status: "Synchronized",
    metrics: [
      { label: "Active Fleet", value: "HR-57-A-1002" },
      { label: "Corridor", value: "Fatehabad ➔ Delhi" },
      { label: "Live Speed", value: "48", unit: "km/h" },
      { label: "Schedule", value: "On Time", unit: "±1m" },
    ],
    explanation: {
      en: "Real-time physical state on highway NH-9. Tap any part or drag to inspect.",
      hi: "हाईवे पर चल रही बस की असली स्थिति। किसी भी हिस्से पर टैप करके जांचें।",
    },
    cameraTarget: { x: 0, y: 1.0, z: 0.2, distance: 5.4 },
  },
  gps: {
    id: "gps",
    name: "GPS & IoT Antenna",
    hindiName: "जीपीएस एंटीना",
    tagline: "Roof Transceiver (Sub-Second Telemetry)",
    status: "Optimal",
    metrics: [
      { label: "Satellites", value: "11", unit: "NavIC" },
      { label: "Ping Rate", value: "3.2", unit: "sec" },
      { label: "Accuracy", value: "< 2.1", unit: "m" },
      { label: "Latency", value: "18", unit: "ms" },
    ],
    explanation: {
      en: "High-precision GPS tracks coordinates across rural corridors via SignalR.",
      hi: "छत पर लगा जीपीएस एंटीना सटीक लोकेशन सेकंड-दर-सेकंड भेजता है।",
    },
    cameraTarget: { x: 0, y: 2.1, z: 0.4, distance: 4.4 },
  },
  cabin: {
    id: "cabin",
    name: "Cabin & Crowd Meter",
    hindiName: "यात्री केबिन एवं भीड़",
    tagline: "Live Door Optical Counters",
    status: "Active",
    metrics: [
      { label: "Occupancy", value: "36 / 52", unit: "seats" },
      { label: "Crowd", value: "Moderate", unit: "69%" },
      { label: "AC Temp", value: "23.5", unit: "°C" },
      { label: "Available", value: "16", unit: "free" },
    ],
    explanation: {
      en: "Door optical sensors track boarding and show empty seats before arrival.",
      hi: "दरवाजों के सेंसर मापते हैं कि बस में कितनी सीटें खाली हैं।",
    },
    cameraTarget: { x: 1.2, y: 1.2, z: -0.1, distance: 4.2 },
  },
  driver: {
    id: "driver",
    name: "Driver Console & ETA",
    hindiName: "चालक केबिन एवं ईटीए",
    tagline: "AI Congestion & Halt Predictor",
    status: "Synchronized",
    metrics: [
      { label: "Next Stand", value: "Bhuna Bypass" },
      { label: "ETA", value: "12:35 PM", unit: "8 mins" },
      { label: "Governor", value: "Active", unit: "65 km/h" },
      { label: "Traffic", value: "Clear", unit: "NH-9" },
    ],
    explanation: {
      en: "Calculates arrival times by factoring road speed, halts, and historical fog.",
      hi: "रास्ते में जाम और स्टॉप के आधार पर पहुंचने का सटीक समय तय करता है।",
    },
    cameraTarget: { x: 0.7, y: 1.3, z: 1.8, distance: 3.8 },
  },
  engine: {
    id: "engine",
    name: "Turbo Powertrain",
    hindiName: "इंजन एवं गति",
    tagline: "OBD-II Real-Time Diagnostics",
    status: "Normal",
    metrics: [
      { label: "Speed", value: "48", unit: "km/h" },
      { label: "Engine Temp", value: "88", unit: "°C" },
      { label: "Fuel Avg", value: "4.8", unit: "km/L" },
      { label: "Battery", value: "24.6", unit: "V" },
    ],
    explanation: {
      en: "Monitors vehicle health and speed compliance in real time.",
      hi: "इंजन सेंसर बस की गति और स्वास्थ्य की लगातार जांच करते हैं।",
    },
    cameraTarget: { x: -0.9, y: 0.8, z: 1.6, distance: 3.9 },
  },
  suspension: {
    id: "suspension",
    name: "Road Router (OSRM)",
    hindiName: "रोड राउटर स्नैपिंग",
    tagline: "Highway Topology Alignment",
    status: "Synchronized",
    metrics: [
      { label: "Highway", value: "NH-9" },
      { label: "Snapping", value: "99.8%", unit: "OSRM" },
      { label: "Covered", value: "64.2", unit: "km" },
      { label: "Remaining", value: "148", unit: "km" },
    ],
    explanation: {
      en: "Snaps raw GPS points to highway curves so the bus moves smoothly on the map.",
      hi: "कच्ची जीपीएस लोकेशन को सड़क के मोड़ों से जोड़ता है।",
    },
    cameraTarget: { x: 1.4, y: 0.5, z: 0.8, distance: 4.2 },
  },
  display: {
    id: "display",
    name: "LED Route Board",
    hindiName: "एलईडी डिस्प्ले बोर्ड",
    tagline: "Bilingual Destination Matrix",
    status: "Optimal",
    metrics: [
      { label: "Route Board", value: "FATEHABAD ➔ DELHI" },
      { label: "Type", value: "Express" },
      { label: "Voice Audio", value: "Hindi / Eng" },
      { label: "Next Stop", value: "Bhuna" },
    ],
    explanation: {
      en: "Displays route destinations and plays spoken Hindi/English arrival audio.",
      hi: "आने वाले स्टॉप की हिंदी और अंग्रेजी में घोषणा करता है।",
    },
    cameraTarget: { x: 0, y: 1.3, z: 2.6, distance: 3.4 },
  },
};

export function BusDigitalTwinViewer({ className }: { className?: string }) {
  const containerRef = useRef<HTMLDivElement>(null);
  const [selectedSubsystem, setSelectedSubsystem] = useState<SubsystemId>("all");
  const [autoRotate, setAutoRotate] = useState(true);
  const activeSubsystem = SUBSYSTEMS[selectedSubsystem];

  // Camera & animation refs
  const targetCameraPos = useRef(new THREE.Vector3(3.8, 1.8, 4.4));
  const currentCameraPos = useRef(new THREE.Vector3(3.8, 1.8, 4.4));
  const targetLookAt = useRef(new THREE.Vector3(0, 0.9, 0.2));
  const currentLookAt = useRef(new THREE.Vector3(0, 0.9, 0.2));

  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;

    // 1. Scene, Camera, Renderer
    const scene = new THREE.Scene();
    scene.background = new THREE.Color(0xf3f8f5);

    const camera = new THREE.PerspectiveCamera(
      38,
      container.clientWidth / container.clientHeight,
      0.1,
      100
    );
    camera.position.set(3.8, 1.8, 4.4);

    const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    renderer.setSize(container.clientWidth, container.clientHeight);
    renderer.shadowMap.enabled = true;
    renderer.shadowMap.type = THREE.PCFSoftShadowMap;
    renderer.toneMapping = THREE.ACESFilmicToneMapping;
    renderer.toneMappingExposure = 1.1;
    container.appendChild(renderer.domElement);

    // 2. Lighting Setup
    const ambientLight = new THREE.AmbientLight(0xffffff, 1.6);
    scene.add(ambientLight);

    const sunLight = new THREE.DirectionalLight(0xfffdf5, 2.4);
    sunLight.position.set(6, 10, 8);
    sunLight.castShadow = true;
    sunLight.shadow.mapSize.width = 1024;
    sunLight.shadow.mapSize.height = 1024;
    sunLight.shadow.bias = -0.0005;
    scene.add(sunLight);

    const fillLight = new THREE.DirectionalLight(0xd1fae5, 1.1);
    fillLight.position.set(-6, 5, -4);
    scene.add(fillLight);

    // 3. Circular Platform (Highway Tarmac)
    const platformGroup = new THREE.Group();
    const platformGeo = new THREE.CylinderGeometry(4.0, 4.0, 0.16, 48);
    const platformMat = new THREE.MeshStandardMaterial({
      color: 0xe2e8f0,
      roughness: 0.85,
      metalness: 0.1,
    });
    const platformMesh = new THREE.Mesh(platformGeo, platformMat);
    platformMesh.position.y = -0.08;
    platformMesh.receiveShadow = true;
    platformGroup.add(platformMesh);

    // Outer emerald rim
    const ringGeo = new THREE.RingGeometry(4.02, 4.1, 64);
    const ringMat = new THREE.MeshBasicMaterial({ color: 0x10b981, side: THREE.DoubleSide });
    const ringMesh = new THREE.Mesh(ringGeo, ringMat);
    ringMesh.rotation.x = -Math.PI / 2;
    ringMesh.position.y = 0.005;
    platformGroup.add(ringMesh);

    // Dashed road lines
    for (let i = 0; i < 14; i++) {
      const angle = (i / 14) * Math.PI * 2;
      const stripeGeo = new THREE.PlaneGeometry(0.12, 0.4);
      const stripeMat = new THREE.MeshBasicMaterial({ color: 0xffffff });
      const stripeMesh = new THREE.Mesh(stripeGeo, stripeMat);
      stripeMesh.rotation.x = -Math.PI / 2;
      stripeMesh.rotation.z = -angle;
      stripeMesh.position.set(Math.cos(angle) * 3.3, 0.01, Math.sin(angle) * 3.3);
      platformGroup.add(stripeMesh);
    }
    scene.add(platformGroup);

    // 4. Detailed 3D Haryana Roadways Express Bus Model
    const busGroup = new THREE.Group();

    // Materials
    const emeraldBodyMat = new THREE.MeshStandardMaterial({
      color: 0x0f766e, // Deep emerald teal
      roughness: 0.35,
      metalness: 0.25,
    });
    const creamStripeMat = new THREE.MeshStandardMaterial({
      color: 0xfef9c3, // Haryana Roadways signature cream stripe
      roughness: 0.4,
      metalness: 0.1,
    });
    const whiteRoofMat = new THREE.MeshStandardMaterial({
      color: 0xf8fafc, // Crisp off-white roof
      roughness: 0.25,
      metalness: 0.1,
    });
    const darkGlassMat = new THREE.MeshPhysicalMaterial({
      color: 0x0f172a,
      roughness: 0.08,
      metalness: 0.1,
      transmission: 0.65,
      transparent: true,
      opacity: 0.88,
    });
    const chromeMat = new THREE.MeshStandardMaterial({
      color: 0xf1f5f9,
      roughness: 0.15,
      metalness: 0.85,
    });
    const rubberMat = new THREE.MeshStandardMaterial({
      color: 0x1e293b,
      roughness: 0.85,
      metalness: 0.15,
    });
    const ledMat = new THREE.MeshBasicMaterial({ color: 0xfbbf24 }); // Amber destination LED
    const headlightMat = new THREE.MeshBasicMaterial({ color: 0xffffff });
    const amberIndicatorMat = new THREE.MeshBasicMaterial({ color: 0xf59e0b });

    // A. Lower Chassis & Green Body
    const lowerBodyGeo = new THREE.BoxGeometry(1.95, 0.85, 5.2);
    const lowerBody = new THREE.Mesh(lowerBodyGeo, emeraldBodyMat);
    lowerBody.position.y = 0.82;
    lowerBody.castShadow = true;
    lowerBody.receiveShadow = true;
    busGroup.add(lowerBody);

    // B. Signature Cream Beltline Stripe
    const stripeGeo = new THREE.BoxGeometry(1.97, 0.16, 5.22);
    const stripeMesh = new THREE.Mesh(stripeGeo, creamStripeMat);
    stripeMesh.position.y = 1.28;
    busGroup.add(stripeMesh);

    // C. Upper Body & Window Band
    const upperBodyGeo = new THREE.BoxGeometry(1.92, 0.85, 5.1);
    const upperBody = new THREE.Mesh(upperBodyGeo, whiteRoofMat);
    upperBody.position.y = 1.76;
    upperBody.castShadow = true;
    busGroup.add(upperBody);

    // D. Panoramic Tinted Windows (Sides)
    const sideWindowGeo = new THREE.BoxGeometry(1.98, 0.58, 4.6);
    const sideWindows = new THREE.Mesh(sideWindowGeo, darkGlassMat);
    sideWindows.position.y = 1.74;
    busGroup.add(sideWindows);

    // Front Windshield (Slanted forward)
    const windshieldGeo = new THREE.BoxGeometry(1.88, 0.68, 0.12);
    const windshield = new THREE.Mesh(windshieldGeo, darkGlassMat);
    windshield.position.set(0, 1.74, 2.52);
    windshield.rotation.x = 0.1;
    busGroup.add(windshield);

    // Dual Windshield Wipers
    for (let w = -0.4; w <= 0.4; w += 0.8) {
      const wiperGeo = new THREE.BoxGeometry(0.03, 0.35, 0.02);
      const wiperMat = new THREE.MeshBasicMaterial({ color: 0x111827 });
      const wiper = new THREE.Mesh(wiperGeo, wiperMat);
      wiper.position.set(w, 1.65, 2.58);
      wiper.rotation.z = -0.3;
      busGroup.add(wiper);
    }

    // Rear Window
    const rearGlassGeo = new THREE.BoxGeometry(1.84, 0.56, 0.08);
    const rearGlass = new THREE.Mesh(rearGlassGeo, darkGlassMat);
    rearGlass.position.set(0, 1.74, -2.54);
    busGroup.add(rearGlass);

    // E. Front Glowing LED Route Board
    const ledBoardGeo = new THREE.BoxGeometry(1.45, 0.2, 0.06);
    const ledBoard = new THREE.Mesh(ledBoardGeo, ledMat);
    ledBoard.position.set(0, 2.12, 2.54);
    busGroup.add(ledBoard);

    // F. Front Chrome Grille & Headlights
    const grilleGeo = new THREE.BoxGeometry(1.2, 0.28, 0.06);
    const grille = new THREE.Mesh(grilleGeo, chromeMat);
    grille.position.set(0, 0.72, 2.62);
    busGroup.add(grille);

    // Front License Plate (HR-57-A-1002)
    const plateGeo = new THREE.BoxGeometry(0.75, 0.14, 0.04);
    const plateMat = new THREE.MeshStandardMaterial({ color: 0xffffff, roughness: 0.3 });
    const plate = new THREE.Mesh(plateGeo, plateMat);
    plate.position.set(0, 0.46, 2.64);
    busGroup.add(plate);

    // Dual High-Intensity Headlights
    [-0.72, 0.72].forEach((xPos) => {
      // Main beam
      const lightGeo = new THREE.CylinderGeometry(0.1, 0.1, 0.06, 16);
      const light = new THREE.Mesh(lightGeo, headlightMat);
      light.rotation.x = Math.PI / 2;
      light.position.set(xPos, 0.75, 2.62);
      busGroup.add(light);

      // Amber turn indicator beside headlight
      const indGeo = new THREE.CylinderGeometry(0.04, 0.04, 0.05, 12);
      const ind = new THREE.Mesh(indGeo, amberIndicatorMat);
      ind.rotation.x = Math.PI / 2;
      ind.position.set(xPos > 0 ? xPos + 0.14 : xPos - 0.14, 0.75, 2.61);
      busGroup.add(ind);
    });

    // Front Chrome Bumper
    const bumperGeo = new THREE.BoxGeometry(2.0, 0.18, 0.16);
    const bumper = new THREE.Mesh(bumperGeo, chromeMat);
    bumper.position.set(0, 0.45, 2.58);
    busGroup.add(bumper);

    // Rear Tail Lights
    [-0.75, 0.75].forEach((xPos) => {
      const tailGeo = new THREE.BoxGeometry(0.14, 0.22, 0.04);
      const tailMat = new THREE.MeshBasicMaterial({ color: 0xef4444 });
      const tail = new THREE.Mesh(tailGeo, tailMat);
      tail.position.set(xPos, 0.9, -2.61);
      busGroup.add(tail);
    });

    // G. Side Chrome View Mirrors
    [-1.08, 1.08].forEach((xPos) => {
      const stalkGeo = new THREE.CylinderGeometry(0.02, 0.02, 0.22, 8);
      const stalk = new THREE.Mesh(stalkGeo, chromeMat);
      stalk.rotation.z = xPos > 0 ? -Math.PI / 3 : Math.PI / 3;
      stalk.position.set(xPos > 0 ? 1.02 : -1.02, 1.62, 2.3);
      busGroup.add(stalk);

      const mirrorGeo = new THREE.BoxGeometry(0.08, 0.26, 0.14);
      const mirror = new THREE.Mesh(mirrorGeo, chromeMat);
      mirror.position.set(xPos > 0 ? 1.15 : -1.15, 1.68, 2.32);
      busGroup.add(mirror);
    });

    // H. Wheels (4 assemblies)
    const wheelsList: THREE.Mesh[] = [];
    const wheelPositions = [
      { x: -0.98, y: 0.38, z: 1.7 },
      { x: 0.98, y: 0.38, z: 1.7 },
      { x: -0.98, y: 0.38, z: -1.7 },
      { x: 0.98, y: 0.38, z: -1.7 },
    ];

    wheelPositions.forEach((pos) => {
      const tireGeo = new THREE.CylinderGeometry(0.38, 0.38, 0.25, 24);
      const tire = new THREE.Mesh(tireGeo, rubberMat);
      tire.rotation.z = Math.PI / 2;
      tire.position.set(pos.x, pos.y, pos.z);
      tire.castShadow = true;

      // Chrome 5-Spoke Alloy Rim
      const rimGeo = new THREE.CylinderGeometry(0.24, 0.24, 0.27, 16);
      const rim = new THREE.Mesh(rimGeo, chromeMat);
      tire.add(rim);

      // Central Hub Nut
      const hubGeo = new THREE.CylinderGeometry(0.08, 0.08, 0.29, 8);
      const hub = new THREE.Mesh(hubGeo, emeraldBodyMat);
      tire.add(hub);

      busGroup.add(tire);
      wheelsList.push(tire);
    });

    // I. Roof-Mounted GPS / IoT Antenna Dome & Animated Radar Rings
    const antennaDomeGeo = new THREE.SphereGeometry(0.18, 20, 16, 0, Math.PI * 2, 0, Math.PI / 2);
    const antennaMat = new THREE.MeshStandardMaterial({
      color: 0x10b981,
      emissive: 0x059669,
      emissiveIntensity: 0.5,
      roughness: 0.2,
    });
    const antennaDome = new THREE.Mesh(antennaDomeGeo, antennaMat);
    antennaDome.position.set(0, 2.22, 0.4);
    busGroup.add(antennaDome);

    const antennaBaseGeo = new THREE.CylinderGeometry(0.25, 0.28, 0.04, 20);
    const antennaBase = new THREE.Mesh(antennaBaseGeo, chromeMat);
    antennaBase.position.set(0, 2.21, 0.4);
    busGroup.add(antennaBase);

    // Pulsating Radar Rings
    const rings: THREE.Mesh[] = [];
    for (let r = 0; r < 2; r++) {
      const pRingGeo = new THREE.RingGeometry(0.22, 0.28, 32);
      const pRingMat = new THREE.MeshBasicMaterial({
        color: 0x34d399,
        side: THREE.DoubleSide,
        transparent: true,
        opacity: 0.8,
      });
      const pRing = new THREE.Mesh(pRingGeo, pRingMat);
      pRing.rotation.x = -Math.PI / 2;
      pRing.position.set(0, 2.28 + r * 0.06, 0.4);
      busGroup.add(pRing);
      rings.push(pRing);
    }

    // Roof AC Module
    const acGeo = new THREE.BoxGeometry(1.2, 0.18, 1.6);
    const acMesh = new THREE.Mesh(acGeo, whiteRoofMat);
    acMesh.position.set(0, 2.28, -0.9);
    acMesh.castShadow = true;
    busGroup.add(acMesh);

    scene.add(busGroup);

    // 5. Interactive Mouse Orbit & Gentle Panoramic Yaw Sway
    let isDragging = false;
    let prevMousePos = { x: 0, y: 0 };
    // Heroic initial angles: facing front-right 3/4 at eye-level
    let theta = 0.52; // ~30° front-right
    let phi = 1.25;   // ~72° eye-level elevation
    let radius = 5.4;

    const onMouseDown = (e: MouseEvent) => {
      isDragging = true;
      prevMousePos = { x: e.clientX, y: e.clientY };
    };

    const onMouseMove = (e: MouseEvent) => {
      if (!isDragging) return;
      const deltaX = e.clientX - prevMousePos.x;
      const deltaY = e.clientY - prevMousePos.y;
      prevMousePos = { x: e.clientX, y: e.clientY };

      theta -= deltaX * 0.008;
      phi = Math.max(0.3, Math.min(Math.PI / 2 - 0.08, phi - deltaY * 0.008));
    };

    const onMouseUp = () => {
      isDragging = false;
    };

    const onWheel = (e: WheelEvent) => {
      e.preventDefault();
      radius = Math.max(3.8, Math.min(8.0, radius + e.deltaY * 0.004));
    };

    // Touch support for mobile devices
    let touchStartDist = 0;
    const onTouchStart = (e: TouchEvent) => {
      if (e.touches.length === 1) {
        isDragging = true;
        prevMousePos = { x: e.touches[0].clientX, y: e.touches[0].clientY };
      } else if (e.touches.length === 2) {
        touchStartDist = Math.hypot(
          e.touches[0].clientX - e.touches[1].clientX,
          e.touches[0].clientY - e.touches[1].clientY
        );
      }
    };

    const onTouchMove = (e: TouchEvent) => {
      if (e.touches.length === 1 && isDragging) {
        const deltaX = e.touches[0].clientX - prevMousePos.x;
        const deltaY = e.touches[0].clientY - prevMousePos.y;
        prevMousePos = { x: e.touches[0].clientX, y: e.touches[0].clientY };
        theta -= deltaX * 0.01;
        phi = Math.max(0.3, Math.min(Math.PI / 2 - 0.08, phi - deltaY * 0.01));
      } else if (e.touches.length === 2) {
        const dist = Math.hypot(
          e.touches[0].clientX - e.touches[1].clientX,
          e.touches[0].clientY - e.touches[1].clientY
        );
        const factor = (touchStartDist - dist) * 0.01;
        touchStartDist = dist;
        radius = Math.max(3.8, Math.min(8.0, radius + factor));
      }
    };

    const onTouchEnd = () => {
      isDragging = false;
    };

    const dom = renderer.domElement;
    dom.addEventListener("mousedown", onMouseDown);
    window.addEventListener("mousemove", onMouseMove);
    window.addEventListener("mouseup", onMouseUp);
    dom.addEventListener("wheel", onWheel, { passive: false });
    dom.addEventListener("touchstart", onTouchStart, { passive: true });
    dom.addEventListener("touchmove", onTouchMove, { passive: true });
    dom.addEventListener("touchend", onTouchEnd, { passive: true });

    // 6. Resize Observer
    const resizeObserver = new ResizeObserver((entries) => {
      if (!entries[0]) return;
      const { width, height } = entries[0].contentRect;
      if (width === 0 || height === 0) return;
      camera.aspect = width / height;
      camera.updateProjectionMatrix();
      renderer.setSize(width, height);
    });
    resizeObserver.observe(container);

    // 7. Animation Loop
    let animationFrameId: number;
    const clock = new THREE.Clock();

    const animate = () => {
      animationFrameId = requestAnimationFrame(animate);
      const elapsed = clock.getElapsedTime();

      // Gentle panoramic yaw sway when idle to keep heroic 3/4 front view in focus
      if (autoRotate && !isDragging) {
        theta = 0.52 + Math.sin(elapsed * 0.45) * 0.38;
        phi = 1.25 + Math.sin(elapsed * 0.3) * 0.05;
      }

      // Camera positioning with smooth lerp
      const camX = radius * Math.sin(phi) * Math.sin(theta);
      const camY = radius * Math.cos(phi);
      const camZ = radius * Math.sin(phi) * Math.cos(theta);
      targetCameraPos.current.set(camX, camY, camZ);

      currentCameraPos.current.lerp(targetCameraPos.current, 0.08);
      camera.position.copy(currentCameraPos.current);

      currentLookAt.current.lerp(targetLookAt.current, 0.08);
      camera.lookAt(currentLookAt.current);

      // Rotate wheels to simulate motion
      wheelsList.forEach((w) => {
        w.rotation.x -= 0.035;
      });

      // Animate pulsing radar rings
      rings.forEach((ring, idx) => {
        const scale = 1 + ((elapsed * 1.5 + idx * 0.6) % 2.0);
        ring.scale.set(scale, scale, 1);
        const mat = ring.material as THREE.MeshBasicMaterial;
        mat.opacity = Math.max(0, 1 - scale / 2.0);
      });

      renderer.render(scene, camera);
    };
    animate();

    return () => {
      cancelAnimationFrame(animationFrameId);
      resizeObserver.disconnect();
      dom.removeEventListener("mousedown", onMouseDown);
      window.removeEventListener("mousemove", onMouseMove);
      window.removeEventListener("mouseup", onMouseUp);
      dom.removeEventListener("wheel", onWheel);
      dom.removeEventListener("touchstart", onTouchStart);
      dom.removeEventListener("touchmove", onTouchMove);
      dom.removeEventListener("touchend", onTouchEnd);
      if (dom.parentElement) {
        dom.parentElement.removeChild(dom);
      }
      renderer.dispose();
    };
  }, [autoRotate]);

  // Handle Subsystem Selection
  const handleSelectSubsystem = (id: SubsystemId) => {
    setSelectedSubsystem(id);
    const sub = SUBSYSTEMS[id];
    if (sub) {
      targetLookAt.current.set(sub.cameraTarget.x, sub.cameraTarget.y, sub.cameraTarget.z);
      transitVoice.speak({
        en: `${sub.name}. ${sub.explanation.en}`,
        hi: `${sub.hindiName}. ${sub.explanation.hi}`,
      });
    }
  };

  const handleVoiceSpeak = () => {
    transitVoice.speak({
      en: `${activeSubsystem.name}. ${activeSubsystem.explanation.en}`,
      hi: `${activeSubsystem.hindiName}. ${activeSubsystem.explanation.hi}`,
    });
  };

  return (
    <div className={cn("flex flex-col overflow-hidden rounded-3xl border border-emerald-200/80 bg-white shadow-lg shadow-emerald-950/5", className)}>
      {/* Top Telemetry Header Bar */}
      <div className="flex items-center justify-between gap-2 border-b border-emerald-100/80 bg-emerald-50/40 px-4 py-2.5">
        <div className="flex items-center gap-2">
          <span className="relative flex h-2 w-2">
            <span className="animate-ping absolute inline-flex h-full w-full rounded-full bg-emerald-400 opacity-75" />
            <span className="relative inline-flex rounded-full h-2 w-2 bg-emerald-500" />
          </span>
          <span className="text-[11px] font-bold uppercase tracking-wider text-emerald-950">
            3D Digital Twin · Telemetry
          </span>
        </div>

        <div className="flex items-center gap-1.5">
          <button
            type="button"
            onClick={() => setAutoRotate((v) => !v)}
            className={cn(
              "inline-flex items-center gap-1 rounded-full px-2.5 py-0.5 text-[11px] font-medium transition-colors border cursor-pointer",
              autoRotate
                ? "border-emerald-300 bg-white text-emerald-800"
                : "border-border/80 bg-card text-muted-foreground hover:bg-muted hover:text-ink"
            )}
            title="Toggle 3D auto sway"
          >
            <RotateCcw className={cn("h-3 w-3", autoRotate && "animate-spin text-emerald-600")} style={{ animationDuration: "6s" }} />
            <span>Sway {autoRotate ? "ON" : "OFF"}</span>
          </button>

          <button
            type="button"
            onClick={handleVoiceSpeak}
            className="inline-flex items-center gap-1 rounded-full border border-emerald-200 bg-white px-2.5 py-0.5 text-[11px] font-semibold text-emerald-800 shadow-2xs hover:bg-emerald-50 cursor-pointer"
            title="Listen to component explanation"
          >
            <Volume2 className="h-3 w-3 text-emerald-600" />
            <span>Audio</span>
          </button>
        </div>
      </div>

      {/* 3D WebGL Canvas Container (Sleek ~300px height) */}
      <div className="relative h-[270px] w-full sm:h-[310px]">
        <div ref={containerRef} className="h-full w-full cursor-grab active:cursor-grabbing" />

        {/* Floating Instruction Badge */}
        <div className="pointer-events-none absolute bottom-2.5 left-3 flex items-center gap-1.5 rounded-full bg-white/90 px-2.5 py-1 text-[10px] font-medium text-ink shadow-xs backdrop-blur-md">
          <Navigation className="h-3 w-3 text-emerald-600 rotate-45" />
          <span>Drag 360° · Tap parts below</span>
        </div>

        {/* Floating Live Signal Badge */}
        <div className="pointer-events-none absolute top-2.5 right-3 flex items-center gap-1.5 rounded-full bg-emerald-900/90 px-2.5 py-1 text-[10px] font-bold text-white shadow-xs backdrop-blur-md">
          <Wifi className="h-3 w-3 text-emerald-400" />
          <span>Live SignalR Ping: 3s</span>
        </div>
      </div>

      {/* Subsystem Pill Selector */}
      <div className="border-t border-emerald-100/80 bg-emerald-50/30 px-3 py-2">
        <div className="flex items-center gap-1.5 overflow-x-auto pb-0.5 scrollbar-none">
          {(
            [
              { id: "all", label: "Overview" },
              { id: "gps", label: "📡 GPS Dome" },
              { id: "cabin", label: "💺 Seats" },
              { id: "driver", label: "⏱️ Smart ETA" },
              { id: "engine", label: "🚀 Engine" },
              { id: "suspension", label: "🛣️ OSRM" },
              { id: "display", label: "📟 LED Board" },
            ] as const
          ).map((item) => {
            const isSelected = selectedSubsystem === item.id;
            return (
              <button
                key={item.id}
                type="button"
                onClick={() => handleSelectSubsystem(item.id)}
                className={cn(
                  "inline-flex items-center gap-1 rounded-full px-3 py-1 text-[11px] font-semibold whitespace-nowrap transition-all cursor-pointer",
                  isSelected
                    ? "bg-emerald-700 text-white shadow-xs ring-1 ring-emerald-500/30 scale-102"
                    : "border border-border/80 bg-white text-muted-foreground hover:bg-emerald-50 hover:text-emerald-900"
                )}
              >
                {item.label}
              </button>
            );
          })}
        </div>
      </div>

      {/* Streamlined Diagnostic Telemetry Drawer (~110px) */}
      <div className="border-t border-emerald-100 bg-white p-3.5 sm:px-4">
        <div className="flex items-center justify-between gap-2">
          <div className="flex items-center gap-1.5 min-w-0">
            <h4 className="font-display text-sm font-bold text-ink truncate">
              {activeSubsystem.name}
            </h4>
            <span className="text-[10px] text-muted-foreground hidden sm:inline">
              ({activeSubsystem.hindiName})
            </span>
          </div>

          <span className="inline-flex items-center gap-1 rounded-full bg-emerald-100 px-2 py-0.5 text-[10px] font-bold text-emerald-800 shrink-0">
            <span className="h-1.5 w-1.5 rounded-full bg-emerald-500 animate-pulse" />
            {activeSubsystem.status}
          </span>
        </div>

        {/* 4 Metrics in a single horizontal row */}
        <div className="mt-2.5 grid grid-cols-4 gap-1.5">
          {activeSubsystem.metrics.map((m, idx) => (
            <div key={idx} className="rounded-lg border border-emerald-100/80 bg-emerald-50/40 px-2 py-1.5">
              <span className="block text-[9px] font-semibold uppercase tracking-wider text-muted-foreground truncate">
                {m.label}
              </span>
              <div className="flex items-baseline gap-0.5 mt-0.5">
                <span className="font-display text-xs font-bold text-emerald-950 truncate">
                  {m.value}
                </span>
                {m.unit && <span className="text-[8px] text-muted-foreground hidden sm:inline">{m.unit}</span>}
              </div>
            </div>
          ))}
        </div>

        {/* 1-Line Explanation */}
        <p className="mt-2 text-[11px] leading-relaxed text-muted-foreground truncate">
          {activeSubsystem.explanation.en}
        </p>
      </div>
    </div>
  );
}
