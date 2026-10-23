export const TenantRole = {
  Customer: 0,
  Staff: 1,
  Owner: 2,
} as const;

export type TenantRole = (typeof TenantRole)[keyof typeof TenantRole];

export const AppointmentStatus = {
  PendingPayment: 0,
  Confirmed: 1,
  Cancelled: 2,
  Completed: 3,
  NoShow: 4,
} as const;

export type AppointmentStatus = (typeof AppointmentStatus)[keyof typeof AppointmentStatus];

export const STATUS_LABELS: Record<number, string> = {
  0: 'Awaiting payment',
  1: 'Confirmed',
  2: 'Cancelled',
  3: 'Completed',
  4: 'No show',
};

export interface MembershipSummary {
  tenantId: string;
  tenantName: string;
  tenantSlug: string;
  role: TenantRole;
}

export interface UserResponse {
  id: string;
  email: string;
  fullName: string;
  memberships: MembershipSummary[];
}

export interface AuthResponse {
  accessToken: string;
  expiresInSeconds: number;
  user: UserResponse;
}

export interface TenantSummary {
  id: string;
  name: string;
  slug: string;
  timeZoneId: string;
  currency: string;
  bookingHorizonDays: number;
}

export interface ServiceResponse {
  id: string;
  name: string;
  description: string | null;
  durationMinutes: number;
  bufferMinutes: number;
  priceMinorUnits: number;
  requiresPrepayment: boolean;
  isActive: boolean;
}

export interface StaffMemberResponse {
  id: string;
  displayName: string;
  title: string | null;
  isActive: boolean;
  serviceIds: string[];
}

export interface BusinessHourResponse {
  id: string;
  dayOfWeek: number;
  startMinute: number;
  endMinute: number;
  staffMemberId: string | null;
}

export interface AvailabilityResponse {
  serviceId: string;
  staffMemberId: string;
  staffDisplayName: string;
  timeZoneId: string;
  slots: string[];
}

export interface AppointmentResponse {
  id: string;
  serviceId: string;
  serviceName: string;
  staffMemberId: string;
  staffDisplayName: string;
  customerName: string;
  customerEmail: string;
  customerPhone: string | null;
  startsAtUtc: string;
  endsAtUtc: string;
  status: AppointmentStatus;
  priceMinorUnits: number;
  currency: string;
  notes: string | null;
  createdAt: string;
}

export interface BookingConfirmation {
  appointment: AppointmentResponse;
  managementToken: string;
  paymentUrl: string | null;
}

export interface MemberResponse {
  userId: string;
  email: string;
  fullName: string;
  role: TenantRole;
  joinedAt: string;
}

export interface AuditEntryResponse {
  id: number;
  action: string;
  entityType: string;
  entityId: string | null;
  detail: string | null;
  actorEmail: string | null;
  createdAt: string;
}

export class ApiError extends Error {
  readonly status: number;

  constructor(status: number, message: string) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
  }
}

/** Prices are integers in minor units, so formatting is the only place they become decimal. */
export function formatMoney(minorUnits: number, currency: string): string {
  return new Intl.NumberFormat(undefined, { style: 'currency', currency }).format(minorUnits / 100);
}

export function formatDuration(minutes: number): string {
  if (minutes < 60) {
    return `${minutes} min`;
  }

  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;

  return rest === 0 ? `${hours} hr` : `${hours} hr ${rest} min`;
}

/** Renders a UTC instant in the business's zone, not the viewer's. */
export function formatInZone(utc: string, timeZone: string, options?: Intl.DateTimeFormatOptions): string {
  return new Intl.DateTimeFormat(undefined, {
    dateStyle: 'medium',
    timeStyle: 'short',
    timeZone,
    ...options,
  }).format(new Date(utc));
}
