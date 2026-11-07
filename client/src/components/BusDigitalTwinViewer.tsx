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
    name: "Complete Digital Twin",
    hindiName: "संपूर्ण 3D बस मॉडल",
    tagline: "Haryana Roadways Express · Fleet Telemetry Online",
    status: "Synchronized",
    metrics: [
      { label: "Active Fleet", value: "HR-57-A-1002" },
      { label: "Corridor", value: "Fatehabad ➔ Delhi" },
      { label: "Live Speed", value: "48", unit: "km/h" },
      { label: "On-Time Status", value: "+2 min", unit: "minor traffic" },
    ],
    explanation: {
      en: "This interactive 3D digital twin reflects the real-time physical state of your bus on the highway. Tap any component or drag to rotate 360 degrees.",
      hi: "यह 3D डिजिटल मॉडल हाईवे पर चल रही बस की असली स्थिति दिखाता है। किसी भी हिस्से पर टैप करके जानकारी देखें।",
    },
    cameraTarget: { x: 0, y: 1.2, z: 0, distance: 7.2 },
  },
  gps: {
    id: "gps",
    name: "GPS & IoT Antenna",
    hindiName: "जीपीएस एवं आईओटी एंटीना",
    tagline: "Roof-Mounted Satellite Transceiver (Sub-Second Telemetry)",
    status: "Optimal",
    metrics: [
      { label: "Satellites Locked", value: "11", unit: "Galileo/NavIC" },
      { label: "Ping Interval", value: "3.2", unit: "seconds" },
      { label: "Accuracy", value: "< 2.1", unit: "meters" },
      { label: "Telemetry Link", value: "4G / SignalR", unit: "18ms latency" },
    ],
    explanation: {
      en: "The roof GPS receiver tracks high-precision coordinates even through rural stretches and tunnels, streaming location pings directly to our ASP.NET Core SignalR hub.",
      hi: "छत पर लगा जीपीएस एंटीना सेटेलाइट से सटीक लोकेशन पकड़ता है और सीधे आपके फोन पर सेकंड-दर-सेकंड बस की लोकेशन भेजता है।",
    },
    cameraTarget: { x: 0, y: 2.1, z: 0.4, distance: 4.8 },
  },
  cabin: {
    id: "cabin",
    name: "Passenger Cabin & Crowd Sensor",
    hindiName: "यात्री केबिन एवं भीड़ मीटर",
    tagline: "Real-Time Seating Occupancy & Airflow Monitoring",
    status: "Active",
    metrics: [
      { label: "Occupied Seats", value: "36 / 52", unit: "seats" },
      { label: "Crowd Level", value: "Moderate", unit: "69% capacity" },
      { label: "AC Temperature", value: "23.5", unit: "°C" },
      { label: "Empty Seats", value: "16", unit: "available" },
    ],
    explanation: {
      en: "Optical door sensors monitor passengers boarding and alighting at each depot, providing commuters with a live crowd meter so you know if seats are available before boarding.",
      hi: "बस के दरवाजों पर लगे सेंसर यात्रियों की संख्या मापते हैं, जिससे आपको बस में चढ़ने से पहले ही पता चल जाता है कि सीटें खाली हैं या नहीं।",
    },
    cameraTarget: { x: 1.5, y: 1.3, z: -0.2, distance: 4.5 },
  },
  driver: {
    id: "driver",
    name: "Driver Console & Smart ETA",
    hindiName: "चालक केबिन एवं स्मार्ट ईटीए",
    tagline: "AI Congestion Detection & Stop Arrival Prediction",
    status: "Synchronized",
    metrics: [
      { label: "Next Stand", value: "Bhuna Bypass" },
      { label: "Estimated Arrival", value: "12:35 PM", unit: "in 8 mins" },
      { label: "Speed Governor", value: "Active", unit: "65 km/h cap" },
      { label: "Highway Delay", value: "+2 min", unit: "fog clearance" },
    ],
    explanation: {
      en: "The driver console monitors traffic density, depot stops, and speed compliance. Machine learning models continuously compute arrival times based on historical road conditions.",
      hi: "चालक कंसोल रास्ते में जाम, धुंध और स्टॉप के आधार पर पहुंचने का सटीक समय तय करता है।",
    },
    cameraTarget: { x: 0.8, y: 1.4, z: 2.3, distance: 4.2 },
  },
  engine: {
    id: "engine",
    name: "Turbo Powertrain & Telemetry",
    hindiName: "इंजन एवं स्पीड मॉनिटर",
    tagline: "Real-Time OBD-II Vehicle Diagnostics & Speed Telemetry",
    status: "Normal",
    metrics: [
      { label: "Vehicle Speed", value: "48", unit: "km/h" },
      { label: "Engine Temp", value: "88", unit: "°C (Optimal)" },
      { label: "Fuel Efficiency", value: "4.8", unit: "km/L" },
      { label: "Battery Telemetry", value: "24.6", unit: "Volts" },
    ],
    explanation: {
      en: "On-board diagnostic sensors monitor bus health, engine temperature, and throttle. If a breakdown occurs, dispatch is alerted automatically and replacement buses are routed.",
      hi: "इंजन सेंसर बस की गति और स्वास्थ्य की लगातार जांच करते हैं ताकि रास्ते में कोई खराबी न आए।",
    },
    cameraTarget: { x: -1.2, y: 0.9, z: 2.0, distance: 4.4 },
  },
  suspension: {
    id: "suspension",
    name: "Road Router & OSRM Snapper",
    hindiName: "रोड राउटर एवं हाईवे स्नैपिंग",
    tagline: "OpenStreetMap Highway Alignment & Waypoint Progression",
    status: "Synchronized",
    metrics: [
      { label: "Active Highway", value: "NH-9 / State Highway 2" },
      { label: "Road Snapping", value: "99.8%", unit: "OSRM match" },
      { label: "Distance Covered", value: "64.2", unit: "km" },
      { label: "Remaining", value: "148.8", unit: "km" },
    ],
    explanation: {
      en: "Raw GPS signals can wander off roads. Our backend road-router snaps coordinates to actual highway geometry, creating a smooth line on your live map without jagged jumps.",
      hi: "हमारा रोड-राउटर कच्ची जीपीएस लोकेशन को असली सड़क के मोड़ों से जोड़ता है ताकि नक्शे पर बस सड़क के बीचों-बीच दिखे।",
    },
    cameraTarget: { x: 1.8, y: 0.6, z: 1.0, distance: 4.8 },
  },
  display: {
    id: "display",
    name: "LED Route Board & Voice",
    hindiName: "एलईडी डिस्प्ले एवं अनाउंसमेंट",
    tagline: "Bilingual Destination Board & Stop Audio Alerts",
    status: "Optimal",
    metrics: [
      { label: "LED Board", value: "FATEHABAD ➔ DELHI" },
      { label: "Service Type", value: "Express Non-Stop" },
      { label: "Audio Lang", value: "Hindi / English" },
      { label: "Next Announcement", value: "Bhuna Stand" },
    ],
    explanation: {
      en: "Dynamic route boards display the destination and upcoming stations. The commuter app plays matching spoken voice announcements so you never miss your stop.",
      hi: "बस के आगे लगा डिजिटल बोर्ड और मोबाइल ऐप दोनों हिंदी और अंग्रेजी में आने वाले स्टॉप की घोषणा करते हैं।",
    },
    cameraTarget: { x: 0, y: 1.5, z: 3.1, distance: 3.8 },
  },
};

export function BusDigitalTwinViewer({ className }: { className?: string }) {
  const containerRef = useRef<HTMLDivElement>(null);
  const [selectedSubsystem, setSelectedSubsystem] = useState<SubsystemId>("all");
  const [autoRotate, setAutoRotate] = useState(true);
  const [isInteracting, setIsInteracting] = useState(false);
  const activeSubsystem = SUBSYSTEMS[selectedSubsystem];

  // Camera & animation refs
  const targetCameraPos = useRef(new THREE.Vector3(5, 3.5, 6));
  const currentCameraPos = useRef(new THREE.Vector3(5, 3.5, 6));
  const targetLookAt = useRef(new THREE.Vector3(0, 1.0, 0));
  const currentLookAt = useRef(new THREE.Vector3(0, 1.0, 0));
  const busGroupRef = useRef<THREE.Group | null>(null);
  const pulseRingsRef = useRef<THREE.Mesh[]>([]);
  const wheelsRef = useRef<THREE.Mesh[]>([]);

  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;

    // 1. Scene, Camera, Renderer
    const scene = new THREE.Scene();
    scene.background = new THREE.Color(0xf6fbf8);

    const camera = new THREE.PerspectiveCamera(
      42,
      container.clientWidth / container.clientHeight,
      0.1,
      100
    );
    camera.position.set(5, 3.5, 6);

    const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    renderer.setSize(container.clientWidth, container.clientHeight);
    renderer.shadowMap.enabled = true;
    renderer.shadowMap.type = THREE.PCFSoftShadowMap;
    renderer.toneMapping = THREE.ACESFilmicToneMapping;
    renderer.toneMappingExposure = 1.05;
    container.appendChild(renderer.domElement);

    // 2. Lighting Setup
    const ambientLight = new THREE.AmbientLight(0xffffff, 1.4);
    scene.add(ambientLight);

    const sunLight = new THREE.DirectionalLight(0xfff7e6, 2.2);
    sunLight.position.set(8, 14, 10);
    sunLight.castShadow = true;
    sunLight.shadow.mapSize.width = 1024;
    sunLight.shadow.mapSize.height = 1024;
    sunLight.shadow.bias = -0.0005;
    scene.add(sunLight);

    const fillLight = new THREE.DirectionalLight(0xdcfce7, 1.0);
    fillLight.position.set(-8, 6, -6);
    scene.add(fillLight);

    // 3. Platform & Highway markings
    const platformGroup = new THREE.Group();

    // Circular tarmac platform
    const platformGeo = new THREE.CylinderGeometry(4.4, 4.4, 0.2, 48);
    const platformMat = new THREE.MeshStandardMaterial({
      color: 0xe2e8f0,
      roughness: 0.85,
      metalness: 0.1,
    });
    const platformMesh = new THREE.Mesh(platformGeo, platformMat);
    platformMesh.position.y = -0.1;
    platformMesh.receiveShadow = true;
    platformGroup.add(platformMesh);

    // Outer circular boundary ring (emerald accent)
    const ringGeo = new THREE.RingGeometry(4.42, 4.52, 64);
    const ringMat = new THREE.MeshBasicMaterial({
      color: 0x10b981,
      side: THREE.DoubleSide,
    });
    const ringMesh = new THREE.Mesh(ringGeo, ringMat);
    ringMesh.rotation.x = -Math.PI / 2;
    ringMesh.position.y = 0.005;
    platformGroup.add(ringMesh);

    // Road dashed stripes
    for (let i = 0; i < 16; i++) {
      const angle = (i / 16) * Math.PI * 2;
      const stripeGeo = new THREE.PlaneGeometry(0.12, 0.45);
      const stripeMat = new THREE.MeshBasicMaterial({ color: 0xffffff });
      const stripeMesh = new THREE.Mesh(stripeGeo, stripeMat);
      stripeMesh.rotation.x = -Math.PI / 2;
      stripeMesh.rotation.z = -angle;
      stripeMesh.position.set(Math.cos(angle) * 3.6, 0.01, Math.sin(angle) * 3.6);
      platformGroup.add(stripeMesh);
    }
    scene.add(platformGroup);

    // 4. Procedural 3D Haryana Roadways Bus Model
    const busGroup = new THREE.Group();
    busGroupRef.current = busGroup;

    // Materials
    const bodyMat = new THREE.MeshStandardMaterial({
      color: 0x0f766e, // Deep emerald teal
      roughness: 0.3,
      metalness: 0.2,
    });
    const roofMat = new THREE.MeshStandardMaterial({
      color: 0xf8fafc, // Crisp white roof
      roughness: 0.2,
      metalness: 0.1,
    });
    const glassMat = new THREE.MeshPhysicalMaterial({
      color: 0x0f172a,
      roughness: 0.1,
      metalness: 0.1,
      transmission: 0.7,
      transparent: true,
      opacity: 0.85,
    });
    const chromeMat = new THREE.MeshStandardMaterial({
      color: 0xf1f5f9,
      roughness: 0.2,
      metalness: 0.8,
    });
    const rubberMat = new THREE.MeshStandardMaterial({
      color: 0x1e293b,
      roughness: 0.8,
      metalness: 0.2,
    });
    const ledGlowMat = new THREE.MeshBasicMaterial({ color: 0xfbbf24 }); // Amber route board

    // A. Bus Lower Body & Chassis
    const lowerBodyGeo = new THREE.BoxGeometry(2.0, 1.2, 5.4);
    const lowerBody = new THREE.Mesh(lowerBodyGeo, bodyMat);
    lowerBody.position.y = 1.0;
    lowerBody.castShadow = true;
    lowerBody.receiveShadow = true;
    busGroup.add(lowerBody);

    // B. Bus Upper Body (Cab & Window Frames)
    const upperBodyGeo = new THREE.BoxGeometry(1.95, 0.95, 5.3);
    const upperBody = new THREE.Mesh(upperBodyGeo, roofMat);
    upperBody.position.y = 1.95;
    upperBody.castShadow = true;
    busGroup.add(upperBody);

    // C. Panoramic Side Windows
    const sideWindowGeo = new THREE.BoxGeometry(2.02, 0.65, 4.8);
    const sideWindows = new THREE.Mesh(sideWindowGeo, glassMat);
    sideWindows.position.y = 1.9;
    busGroup.add(sideWindows);

    // Front Windshield
    const windshieldGeo = new THREE.BoxGeometry(1.92, 0.75, 0.2);
    const windshield = new THREE.Mesh(windshieldGeo, glassMat);
    windshield.position.set(0, 1.9, 2.62);
    windshield.rotation.x = 0.12;
    busGroup.add(windshield);

    // Rear Window
    const rearGlassGeo = new THREE.BoxGeometry(1.9, 0.65, 0.1);
    const rearGlass = new THREE.Mesh(rearGlassGeo, glassMat);
    rearGlass.position.set(0, 1.9, -2.66);
    busGroup.add(rearGlass);

    // D. Front LED Destination Board (Glowing Amber)
    const boardGeo = new THREE.BoxGeometry(1.5, 0.22, 0.08);
    const ledBoard = new THREE.Mesh(boardGeo, ledGlowMat);
    ledBoard.position.set(0, 2.32, 2.65);
    busGroup.add(ledBoard);

    // Destination board frame
    const boardFrameGeo = new THREE.BoxGeometry(1.56, 0.26, 0.06);
    const boardFrame = new THREE.Mesh(boardFrameGeo, bodyMat);
    boardFrame.position.set(0, 2.32, 2.62);
    busGroup.add(boardFrame);

    // E. Front Headlights & Chrome Grille
    const headlightMat = new THREE.MeshBasicMaterial({ color: 0xffffff });
    const leftLight = new THREE.Mesh(new THREE.CylinderGeometry(0.12, 0.12, 0.08, 16), headlightMat);
    leftLight.rotation.x = Math.PI / 2;
    leftLight.position.set(-0.75, 0.75, 2.72);
    busGroup.add(leftLight);

    const rightLight = new THREE.Mesh(new THREE.CylinderGeometry(0.12, 0.12, 0.08, 16), headlightMat);
    rightLight.rotation.x = Math.PI / 2;
    rightLight.position.set(0.75, 0.75, 2.72);
    busGroup.add(rightLight);

    // Chrome Bumper
    const bumperGeo = new THREE.BoxGeometry(2.05, 0.2, 0.2);
    const bumper = new THREE.Mesh(bumperGeo, chromeMat);
    bumper.position.set(0, 0.5, 2.7);
    busGroup.add(bumper);

    // F. Wheels (4 sets: 2 front, 2 dual rear)
    const wheelsList: THREE.Mesh[] = [];
    const wheelPositions = [
      { x: -1.02, y: 0.42, z: 1.8 },
      { x: 1.02, y: 0.42, z: 1.8 },
      { x: -1.02, y: 0.42, z: -1.8 },
      { x: 1.02, y: 0.42, z: -1.8 },
    ];

    wheelPositions.forEach((pos) => {
      const tireGeo = new THREE.CylinderGeometry(0.42, 0.42, 0.28, 24);
      const tire = new THREE.Mesh(tireGeo, rubberMat);
      tire.rotation.z = Math.PI / 2;
      tire.position.set(pos.x, pos.y, pos.z);
      tire.castShadow = true;

      // Silver Rim
      const rimGeo = new THREE.CylinderGeometry(0.25, 0.25, 0.3, 16);
      const rim = new THREE.Mesh(rimGeo, chromeMat);
      tire.add(rim);

      busGroup.add(tire);
      wheelsList.push(tire);
    });
    wheelsRef.current = wheelsList;

    // G. Roof-Mounted GPS / IoT Antenna Dome & Pulse Ring
    const antennaDomeGeo = new THREE.SphereGeometry(0.22, 24, 16, 0, Math.PI * 2, 0, Math.PI / 2);
    const antennaMat = new THREE.MeshStandardMaterial({
      color: 0x10b981,
      roughness: 0.2,
      emissive: 0x059669,
      emissiveIntensity: 0.4,
    });
    const antennaDome = new THREE.Mesh(antennaDomeGeo, antennaMat);
    antennaDome.position.set(0, 2.45, 0.5);
    busGroup.add(antennaDome);

    // Antenna Base
    const antennaBaseGeo = new THREE.CylinderGeometry(0.3, 0.32, 0.06, 24);
    const antennaBase = new THREE.Mesh(antennaBaseGeo, chromeMat);
    antennaBase.position.set(0, 2.44, 0.5);
    busGroup.add(antennaBase);

    // Animated Pulsating Telemetry Rings
    const rings: THREE.Mesh[] = [];
    for (let r = 0; r < 2; r++) {
      const pRingGeo = new THREE.RingGeometry(0.28, 0.35, 32);
      const pRingMat = new THREE.MeshBasicMaterial({
        color: 0x34d399,
        side: THREE.DoubleSide,
        transparent: true,
        opacity: 0.8,
      });
      const pRing = new THREE.Mesh(pRingGeo, pRingMat);
      pRing.rotation.x = -Math.PI / 2;
      pRing.position.set(0, 2.52 + r * 0.08, 0.5);
      busGroup.add(pRing);
      rings.push(pRing);
    }
    pulseRingsRef.current = rings;

    // H. Roof AC Unit
    const acGeo = new THREE.BoxGeometry(1.3, 0.22, 1.8);
    const acMesh = new THREE.Mesh(acGeo, roofMat);
    acMesh.position.set(0, 2.54, -1.0);
    acMesh.castShadow = true;
    busGroup.add(acMesh);

    scene.add(busGroup);

    // 5. Interactive Orbit & Mouse Controls
    let isDragging = false;
    let prevMousePos = { x: 0, y: 0 };
    let theta = Math.PI / 4;
    let phi = Math.PI / 4;
    let radius = 7.5;

    const onMouseDown = (e: MouseEvent) => {
      isDragging = true;
      setIsInteracting(true);
      prevMousePos = { x: e.clientX, y: e.clientY };
    };

    const onMouseMove = (e: MouseEvent) => {
      if (!isDragging) return;
      const deltaX = e.clientX - prevMousePos.x;
      const deltaY = e.clientY - prevMousePos.y;
      prevMousePos = { x: e.clientX, y: e.clientY };

      theta -= deltaX * 0.008;
      phi = Math.max(0.15, Math.min(Math.PI / 2 - 0.05, phi - deltaY * 0.008));
    };

    const onMouseUp = () => {
      isDragging = false;
      setTimeout(() => setIsInteracting(false), 2000);
    };

    const onWheel = (e: WheelEvent) => {
      e.preventDefault();
      radius = Math.max(4.0, Math.min(10.5, radius + e.deltaY * 0.004));
    };

    // Touch support for mobile devices
    let touchStartDist = 0;
    const onTouchStart = (e: TouchEvent) => {
      if (e.touches.length === 1) {
        isDragging = true;
        setIsInteracting(true);
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
        phi = Math.max(0.15, Math.min(Math.PI / 2 - 0.05, phi - deltaY * 0.01));
      } else if (e.touches.length === 2) {
        const dist = Math.hypot(
          e.touches[0].clientX - e.touches[1].clientX,
          e.touches[0].clientY - e.touches[1].clientY
        );
        const factor = (touchStartDist - dist) * 0.01;
        touchStartDist = dist;
        radius = Math.max(4.0, Math.min(10.5, radius + factor));
      }
    };

    const onTouchEnd = () => {
      isDragging = false;
      setTimeout(() => setIsInteracting(false), 2000);
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
    let clock = new THREE.Clock();

    const animate = () => {
      animationFrameId = requestAnimationFrame(animate);
      const elapsed = clock.getElapsedTime();

      // Auto-rotation when not manually dragging
      if (autoRotate && !isDragging) {
        theta += 0.004;
      }

      // Smooth camera position spherical coordinates
      const camX = radius * Math.sin(phi) * Math.sin(theta);
      const camY = radius * Math.cos(phi);
      const camZ = radius * Math.sin(phi) * Math.cos(theta);
      targetCameraPos.current.set(camX, camY, camZ);

      currentCameraPos.current.lerp(targetCameraPos.current, 0.08);
      camera.position.copy(currentCameraPos.current);

      currentLookAt.current.lerp(targetLookAt.current, 0.08);
      camera.lookAt(currentLookAt.current);

      // Animate wheels rotation (simulating highway speed)
      wheelsList.forEach((w) => {
        w.rotation.x -= 0.035;
      });

      // Animate pulsing GPS telemetry radar rings
      rings.forEach((ring, idx) => {
        const scale = 1 + ((elapsed * 1.6 + idx * 0.7) % 2.2);
        ring.scale.set(scale, scale, 1);
        const mat = ring.material as THREE.MeshBasicMaterial;
        mat.opacity = Math.max(0, 1 - scale / 2.2);
      });

      renderer.render(scene, camera);
    };
    animate();

    // Cleanup
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
      // Announce via Voice if enabled
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
    <div className={cn("relative flex flex-col overflow-hidden rounded-3xl border border-emerald-200/60 bg-gradient-to-b from-emerald-50/40 via-white to-white shadow-xl shadow-emerald-950/5", className)}>
      {/* Top Telemetry Header Bar */}
      <div className="flex flex-wrap items-center justify-between gap-3 border-b border-emerald-100/80 bg-white/70 px-5 py-3 backdrop-blur-md">
        <div className="flex items-center gap-2">
          <span className="relative flex h-2.5 w-2.5">
            <span className="animate-ping absolute inline-flex h-full w-full rounded-full bg-emerald-400 opacity-75" />
            <span className="relative inline-flex rounded-full h-2.5 w-2.5 bg-emerald-500" />
          </span>
          <span className="text-xs font-bold uppercase tracking-wider text-emerald-900">
            3D Digital Twin · Telemetry Engine
          </span>
        </div>

        <div className="flex items-center gap-2">
          <button
            type="button"
            onClick={() => setAutoRotate((v) => !v)}
            className={cn(
              "inline-flex items-center gap-1.5 rounded-full px-3 py-1 text-xs font-medium transition-colors border",
              autoRotate
                ? "border-emerald-300 bg-emerald-50 text-emerald-800"
                : "border-border/80 bg-card text-muted-foreground hover:bg-muted hover:text-ink"
            )}
            title="Toggle 3D auto rotation"
          >
            <RotateCcw className={cn("h-3 w-3", autoRotate && "animate-spin text-emerald-600")} style={{ animationDuration: "6s" }} />
            <span>Auto-Rotate {autoRotate ? "ON" : "OFF"}</span>
          </button>

          <button
            type="button"
            onClick={handleVoiceSpeak}
            className="inline-flex items-center gap-1.5 rounded-full border border-emerald-200 bg-white px-3 py-1 text-xs font-semibold text-emerald-800 shadow-2xs hover:bg-emerald-50"
            title="Listen to subsystem explanation"
          >
            <Volume2 className="h-3.5 w-3.5 text-emerald-600" />
            <span>Listen</span>
          </button>
        </div>
      </div>

      {/* 3D WebGL Canvas Container */}
      <div className="relative h-[380px] w-full sm:h-[460px] md:h-[500px]">
        <div ref={containerRef} className="h-full w-full cursor-grab active:cursor-grabbing" />

        {/* Floating Instruction Badge */}
        <div className="pointer-events-none absolute bottom-3 left-4 flex items-center gap-2 rounded-full bg-white/85 px-3 py-1.5 text-[11px] font-medium text-ink shadow-sm backdrop-blur-md ring-1 ring-black/5">
          <Navigation className="h-3 w-3 text-emerald-600 rotate-45" />
          <span>Drag to rotate 360° · Tap parts below to inspect</span>
        </div>

        {/* Floating Live Signal Badge */}
        <div className="pointer-events-none absolute top-3 right-4 flex items-center gap-2 rounded-full bg-emerald-900/90 px-3 py-1.5 text-[11px] font-bold text-white shadow-md backdrop-blur-md">
          <Wifi className="h-3 w-3 text-emerald-400" />
          <span>Live GPS Ping: 3s</span>
        </div>
      </div>

      {/* Subsystem Pill Selector */}
      <div className="border-t border-emerald-100/80 bg-emerald-50/50 p-3 sm:px-5">
        <div className="flex items-center gap-1.5 overflow-x-auto pb-1 scrollbar-none">
          {(
            [
              { id: "all", label: "Overview" },
              { id: "gps", label: "📡 GPS Receiver" },
              { id: "cabin", label: "💺 Seats & Crowd" },
              { id: "driver", label: "⏱️ Smart ETA" },
              { id: "engine", label: "🚀 Engine & Speed" },
              { id: "suspension", label: "🛣️ Road Router" },
              { id: "display", label: "📟 LED Announcer" },
            ] as const
          ).map((item) => {
            const isSelected = selectedSubsystem === item.id;
            return (
              <button
                key={item.id}
                type="button"
                onClick={() => handleSelectSubsystem(item.id)}
                className={cn(
                  "inline-flex items-center gap-1.5 rounded-full px-3.5 py-1.5 text-xs font-semibold whitespace-nowrap transition-all cursor-pointer",
                  isSelected
                    ? "bg-emerald-700 text-white shadow-sm ring-2 ring-emerald-500/30 scale-105"
                    : "border border-emerald-200/80 bg-white/90 text-emerald-950 hover:bg-emerald-100/70"
                )}
              >
                {item.label}
              </button>
            );
          })}
        </div>
      </div>

      {/* Diagnostic Inspection Card */}
      <div className="border-t border-emerald-100 bg-white p-5">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <div>
            <div className="flex items-center gap-2">
              <h3 className="font-display text-lg font-bold text-ink">
                {activeSubsystem.name}
              </h3>
              <span className="text-xs text-muted-foreground">({activeSubsystem.hindiName})</span>
            </div>
            <p className="mt-0.5 text-xs text-muted-foreground">{activeSubsystem.tagline}</p>
          </div>

          <span className="inline-flex items-center gap-1.5 rounded-full bg-emerald-100 px-3 py-1 text-xs font-bold text-emerald-800 dark:bg-emerald-950 dark:text-emerald-300">
            <span className="h-1.5 w-1.5 rounded-full bg-emerald-500 animate-pulse" />
            {activeSubsystem.status}
          </span>
        </div>

        {/* Live Metrics Grid */}
        <div className="mt-4 grid grid-cols-2 gap-2.5 sm:grid-cols-4">
          {activeSubsystem.metrics.map((m, idx) => (
            <div key={idx} className="rounded-xl border border-emerald-100/80 bg-emerald-50/40 p-2.5">
              <span className="text-[10px] font-semibold uppercase tracking-wider text-muted-foreground">
                {m.label}
              </span>
              <div className="mt-1 flex items-baseline gap-1">
                <span className="font-display text-base font-bold text-emerald-950">
                  {m.value}
                </span>
                {m.unit && <span className="text-[10px] text-muted-foreground">{m.unit}</span>}
              </div>
            </div>
          ))}
        </div>

        {/* Clear Plain-Language Explanation */}
        <div className="mt-4 rounded-xl border border-border/70 bg-card p-3.5 text-xs leading-relaxed text-ink/80">
          <p className="font-medium">{activeSubsystem.explanation.en}</p>
          <p className="mt-1.5 text-muted-foreground font-hindi">{activeSubsystem.explanation.hi}</p>
        </div>
      </div>
    </div>
  );
}
