export type AuthUser = {
  fullName: string;
  email: string;
  role: string;
  driverCode?: string | null;
  phone?: string | null;
};

export type StopOption = {
  id: number;
  name: string;
  code: string;
  city: string | null;
  address: string | null;
  latitude: number;
  longitude: number;
};

export type StopOnTrip = {
  stopId: number;
  name: string;
  code: string;
  sequence: number;
  latitude: number;
  longitude: number;
  distanceKm: number;
  scheduledTime: string;
  status: string;
  etaMinutes: number | null;
  scheduledArrival?: string | null;
  estimatedArrival?: string | null;
  actualArrival?: string | null;
  scheduledDeparture?: string | null;
  estimatedDeparture?: string | null;
  actualDeparture?: string | null;
  delayMinutes?: number | null;
  displayStatus?: string | null;
};

export type TrackSnapshot = {
  tripId: number | null;
  scheduleId: number | null;
  busId: number;
  routeId: number;
  fleetNumber: string;
  registrationNumber: string;
  busType: string;
  capacity: number;
  operatorName: string;
  routeCode: string;
  routeName: string;
  status: string;
  driverName: string | null;
  latitude: number | null;
  longitude: number | null;
  heading: number | null;
  speedKmph: number | null;
  recordedAt: string | null;
  currentStop: string | null;
  nextStop: string | null;
  etaMinutes: number | null;
  delayMinutes: number | null;
  outsideSlot: boolean;
  simulation: boolean;
  operationsLive: boolean;
  departureTime: string;
  actualDepartureTime?: string | null;
  estimatedDepartureTime?: string | null;
  delayStatus?: string | null;
  lastEtaUpdate?: string | null;
  serviceDate: string;
  fare: number | null;
  fromStop: string | null;
  toStop: string | null;
  etaToYouMinutes: number | null;
  boardingPassed: boolean;
  etaToDestinationMinutes: number | null;
  departsInMinutes: number | null;
  stops: StopOnTrip[];
  path: { latitude: number; longitude: number }[];
};

export type SearchResponse = {
  from: string;
  to: string;
  date: string;
  results: TrackSnapshot[];
};

export type Favorite = {
  id: number;
  busId: number | null;
  fleetNumber: string | null;
  registrationNumber: string | null;
  routeId: number | null;
  routeCode: string | null;
  fromStop: string | null;
  toStop: string | null;
  fromStopId: number | null;
  toStopId: number | null;
  label: string | null;
  createdAt: string;
};

export type Notice = {
  id: number;
  tripId: number | null;
  title: string;
  body: string;
  kind: string;
  isRead: boolean;
  createdAt: string;
};

export type PublicRouteStop = {
  stopId: number;
  name: string;
  code: string;
  sequenceNo: number;
  latitude: number;
  longitude: number;
  distanceKm: number;
};

export type PublicRoute = {
  id: number;
  code: string;
  name: string;
  distanceKm: number | null;
  origin: string;
  destination: string;
  stops: PublicRouteStop[];
  path: { latitude: number; longitude: number }[];
  liveCount: number;
  hasLiveBus: boolean;
  runningBuses: TrackSnapshot[];
};
