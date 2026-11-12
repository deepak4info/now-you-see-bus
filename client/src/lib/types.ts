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
  busName?: string;
  busType: string;
  capacity: number;
  operatorName: string;
  routeCode: string;
  routeName: string;
  originStop?: string | null;
  destinationStop?: string | null;
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
  estimatedArrivalTime?: string | null;
  actualArrivalTime?: string | null;
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

export type JourneyLeg = {
  routeId: number;
  routeCode: string;
  routeName: string;
  scheduleId: number | null;
  tripId: number | null;
  fleetNumber: string | null;
  busType: string | null;
  operatorName: string | null;
  fromStop: string;
  toStop: string;
  departureTime: string;
  arrivalTime: string;
  durationMinutes: number;
  estimatedFare: number;
  distanceKm: number;
  status: string;
};

export type JourneyOption = {
  optionId: string;
  isDirect: boolean;
  transfersCount: number;
  transferStops: string[];
  departureTime: string;
  arrivalTime: string;
  totalJourneyMinutes: number;
  totalWaitMinutes: number;
  estimatedTotalFare: number;
  totalDistanceKm: number;
  legs: JourneyLeg[];
  summary: string;
};

export type MultiRoutePlan = {
  from: string;
  to: string;
  date: string;
  directAvailable: boolean;
  directOptions: JourneyOption[];
  transferOptions: JourneyOption[];
  allOptions: JourneyOption[];
};

export type SearchResponse = {
  from: string;
  to: string;
  date: string;
  requestedDate?: string;
  showingNextDate?: boolean;
  results: TrackSnapshot[];
  journeyPlan?: MultiRoutePlan | null;
};

export type BusTrackingItem = {
  tripId: number | null;
  scheduleId: number | null;
  busId: number;
  routeId: number;
  fleetNumber: string;
  registrationNumber: string;
  busName: string;
  busType: string;
  operatorName: string;
  routeCode: string;
  routeName: string;
  originStop: string;
  destinationStop: string;
  currentLocation: string | null;
  nextStop: string | null;
  etaMinutes: number | null;
  departureTime: string;
  actualDepartureTime: string | null;
  estimatedArrivalTime: string | null;
  actualArrivalTime: string | null;
  serviceDate: string;
  status: string;
  delayMinutes: number | null;
  delayStatus: string | null;
  recordedAt: string | null;
  latitude: number | null;
  longitude: number | null;
  operationsLive: boolean;
  speedKmph?: number | null;
};

export type DelayAlertEvent = {
  tripId: number;
  fleetNumber: string;
  routeCode: string;
  routeName: string;
  delayMinutes: number;
  nextStop?: string | null;
  estimatedArrivalTime?: string | null;
  recordedAt?: string | null;
  title: string;
  message: string;
};

export type PaginatedBusTracking = {
  items: BusTrackingItem[];
  page: number;
  pageSize: number;
  totalCount: number;
  hasMore: boolean;
};

export type TrackingFilterOperatorOption = {
  id: number;
  name: string;
  code: string;
};

export type TrackingFilterRouteOption = {
  id: number;
  code: string;
  name: string;
  originStop: string;
  destinationStop: string;
};

export type TrackingFilterOptions = {
  operators: TrackingFilterOperatorOption[];
  routes: TrackingFilterRouteOption[];
  busTypes: string[];
};

export type Favorite = {
  id: number;
  kind?: "bus" | "route" | "stop";
  busId: number | null;
  fleetNumber: string | null;
  registrationNumber: string | null;
  routeId: number | null;
  routeCode: string | null;
  routeName?: string | null;
  fromStop: string | null;
  fromStopCode?: string | null;
  toStop: string | null;
  toStopCode?: string | null;
  fromStopId: number | null;
  toStopId: number | null;
  label: string | null;
  createdAt: string;
};

export type StopReminderItem = {
  id: number;
  tripId: number | null;
  routeId: number;
  stopId: number;
  stopName: string;
  fleetNumber: string;
  remindBeforeMinutes: number;
  targetDate: string;
  isTriggered: boolean;
  triggeredAt: string | null;
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

export type CommunityUpdateItem = {
  id: number;
  category: string;
  fleetNumber: string;
  routeId: number | null;
  routeCode: string | null;
  routeName: string | null;
  tripId: number | null;
  reportedStopName: string | null;
  proposedTime: string | null;
  delayMinutes: number | null;
  description: string;
  status: string;
  adminNotes: string | null;
  verifiedAt: string | null;
  verifiedBy: string | null;
  createdAt: string;
};
