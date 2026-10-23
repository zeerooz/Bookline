import {
  ApiError,
  type AppointmentResponse,
  type AuditEntryResponse,
  type AuthResponse,
  type AvailabilityResponse,
  type BookingConfirmation,
  type BusinessHourResponse,
  type MemberResponse,
  type ServiceResponse,
  type StaffMemberResponse,
  type TenantRole,
  type TenantSummary,
  type UserResponse,
} from './types';

const BASE_URL = import.meta.env.VITE_API_BASE_URL ?? '';
const TOKEN_KEY = 'bookline.token';

/**
 * The access token is kept in localStorage.
 *
 * This is a different judgement from a password manager, and worth stating:
 * here the token guards a business calendar rather than a vault of secrets,
 * and staff reasonably expect to stay signed in between visits. The exposure
 * is that any script on this origin could read it, which is why the API keeps
 * tokens short-lived and reads roles from the database on every request rather
 * than trusting claims inside the token.
 */
function readToken(): string | null {
  try {
    return localStorage.getItem(TOKEN_KEY);
  } catch {
    // Private browsing, or storage blocked entirely.
    return null;
  }
}

export function setToken(token: string | null): void {
  try {
    if (token) {
      localStorage.setItem(TOKEN_KEY, token);
    } else {
      localStorage.removeItem(TOKEN_KEY);
    }
  } catch {
    // Not fatal: the session simply will not survive a reload.
  }
}

export function hasToken(): boolean {
  return readToken() !== null;
}

let onSessionLost: (() => void) | null = null;

export function setSessionLostHandler(handler: (() => void) | null): void {
  onSessionLost = handler;
}

async function toError(response: Response): Promise<ApiError> {
  let message = `Request failed (HTTP ${response.status}).`;

  try {
    const problem = (await response.json()) as { detail?: string; title?: string };
    message = problem.detail ?? problem.title ?? message;
  } catch {
    // A non-JSON error body is not worth surfacing verbatim.
  }

  return new ApiError(response.status, message);
}

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers);
  headers.set('Accept', 'application/json');

  if (init.body !== undefined) {
    headers.set('Content-Type', 'application/json');
  }

  const token = readToken();

  if (token) {
    headers.set('Authorization', `Bearer ${token}`);
  }

  const response = await fetch(`${BASE_URL}${path}`, { ...init, headers });

  if (response.status === 401 && token) {
    // The token expired or was revoked. There is no refresh flow, so the only
    // honest move is to send the user back to the sign-in page.
    setToken(null);
    onSessionLost?.();
  }

  if (!response.ok) {
    throw await toError(response);
  }

  if (response.status === 204) {
    return undefined as T;
  }

  return (await response.json()) as T;
}

const json = (body: unknown): RequestInit => ({ body: JSON.stringify(body) });

export const api = {
  // ---- accounts -------------------------------------------------------
  register: (payload: { email: string; password: string; fullName: string }) =>
    request<AuthResponse>('/api/auth/register', { method: 'POST', ...json(payload) }),

  login: (payload: { email: string; password: string }) =>
    request<AuthResponse>('/api/auth/login', { method: 'POST', ...json(payload) }),

  me: () => request<UserResponse>('/api/auth/me'),

  createBusiness: (payload: {
    name: string;
    slug: string;
    timeZoneId: string;
    currency: string;
    minimumNoticeMinutes: number;
    bookingHorizonDays: number;
  }) => request<TenantSummary>('/api/businesses', { method: 'POST', ...json(payload) }),

  // ---- public booking --------------------------------------------------
  publicBusiness: (slug: string) => request<TenantSummary>(`/api/public/${slug}`),

  publicServices: (slug: string) => request<ServiceResponse[]>(`/api/public/${slug}/services`),

  publicStaff: (slug: string) => request<StaffMemberResponse[]>(`/api/public/${slug}/staff`),

  availability: (slug: string, serviceId: string, from: string, to: string, staffMemberId?: string) =>
    request<AvailabilityResponse[]>(
      `/api/public/${slug}/availability?serviceId=${serviceId}&from=${from}&to=${to}` +
        (staffMemberId ? `&staffMemberId=${staffMemberId}` : ''),
    ),

  book: (
    slug: string,
    payload: {
      serviceId: string;
      staffMemberId?: string | null;
      startsAtUtc: string;
      customerName: string;
      customerEmail: string;
      customerPhone?: string;
      notes?: string;
    },
  ) => request<BookingConfirmation>(`/api/public/${slug}/bookings`, { method: 'POST', ...json(payload) }),

  getBooking: (slug: string, token: string) =>
    request<AppointmentResponse>(`/api/public/${slug}/bookings/${token}`),

  cancelBooking: (slug: string, token: string, reason?: string) =>
    request<AppointmentResponse>(`/api/public/${slug}/bookings/${token}/cancel`, {
      method: 'POST',
      ...json({ reason }),
    }),

  // ---- management ------------------------------------------------------
  services: (slug: string) => request<ServiceResponse[]>(`/api/t/${slug}/services`),

  createService: (slug: string, payload: unknown) =>
    request<ServiceResponse>(`/api/t/${slug}/services`, { method: 'POST', ...json(payload) }),

  updateService: (slug: string, id: string, payload: unknown) =>
    request<ServiceResponse>(`/api/t/${slug}/services/${id}`, { method: 'PUT', ...json(payload) }),

  deactivateService: (slug: string, id: string) =>
    request<void>(`/api/t/${slug}/services/${id}`, { method: 'DELETE' }),

  staff: (slug: string) => request<StaffMemberResponse[]>(`/api/t/${slug}/staff`),

  createStaff: (slug: string, payload: unknown) =>
    request<StaffMemberResponse>(`/api/t/${slug}/staff`, { method: 'POST', ...json(payload) }),

  updateStaff: (slug: string, id: string, payload: unknown) =>
    request<StaffMemberResponse>(`/api/t/${slug}/staff/${id}`, { method: 'PUT', ...json(payload) }),

  deactivateStaff: (slug: string, id: string) =>
    request<void>(`/api/t/${slug}/staff/${id}`, { method: 'DELETE' }),

  hours: (slug: string) => request<BusinessHourResponse[]>(`/api/t/${slug}/hours`),

  replaceHours: (slug: string, payload: unknown) =>
    request<BusinessHourResponse[]>(`/api/t/${slug}/hours`, { method: 'PUT', ...json(payload) }),

  appointments: (slug: string, fromUtc: string, toUtc: string) =>
    request<AppointmentResponse[]>(`/api/t/${slug}/appointments?fromUtc=${fromUtc}&toUtc=${toUtc}`),

  cancelAppointment: (slug: string, id: string, reason?: string) =>
    request<void>(`/api/t/${slug}/appointments/${id}/cancel`, { method: 'POST', ...json({ reason }) }),

  completeAppointment: (slug: string, id: string) =>
    request<void>(`/api/t/${slug}/appointments/${id}/complete`, { method: 'POST' }),

  noShowAppointment: (slug: string, id: string) =>
    request<void>(`/api/t/${slug}/appointments/${id}/no-show`, { method: 'POST' }),

  members: (slug: string) => request<MemberResponse[]>(`/api/t/${slug}/members`),

  addMember: (slug: string, payload: { email: string; role: TenantRole }) =>
    request<MemberResponse>(`/api/t/${slug}/members`, { method: 'POST', ...json(payload) }),

  removeMember: (slug: string, userId: string) =>
    request<void>(`/api/t/${slug}/members/${userId}`, { method: 'DELETE' }),

  audit: (slug: string) => request<AuditEntryResponse[]>(`/api/t/${slug}/audit`),
};
