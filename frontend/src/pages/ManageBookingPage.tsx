import { useEffect, useState } from 'react';
import { useParams } from 'react-router-dom';

import { api } from '../api/client';
import {
  AppointmentStatus,
  STATUS_LABELS,
  formatInZone,
  formatMoney,
  type AppointmentResponse,
  type TenantSummary,
} from '../api/types';

/**
 * What a guest sees when they follow the link from their confirmation email.
 * The token in the URL is the credential; there is nothing else to sign in with.
 */
export function ManageBookingPage() {
  const { slug = '', token = '' } = useParams();

  const [business, setBusiness] = useState<TenantSummary | null>(null);
  const [appointment, setAppointment] = useState<AppointmentResponse | null>(null);
  const [notFound, setNotFound] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    Promise.all([api.publicBusiness(slug), api.getBooking(slug, token)])
      .then(([tenant, booking]) => {
        setBusiness(tenant);
        setAppointment(booking);
      })
      .catch(() => setNotFound(true));
  }, [slug, token]);

  async function cancel() {
    if (!window.confirm('Cancel this booking? This cannot be undone.')) {
      return;
    }

    setBusy(true);
    setError(null);

    try {
      setAppointment(await api.cancelBooking(slug, token));
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Could not cancel the booking.');
    } finally {
      setBusy(false);
    }
  }

  if (notFound) {
    return (
      <div className="page page-narrow">
        <div className="empty">
          <h1>Booking not found</h1>
          <p>This link is wrong, or the booking has been removed. Check the link in your confirmation email.</p>
        </div>
      </div>
    );
  }

  if (!appointment || !business) {
    return (
      <div className="page page-narrow">
        <p className="empty">Loading…</p>
      </div>
    );
  }

  const cancelled = appointment.status === AppointmentStatus.Cancelled;
  const past = new Date(appointment.startsAtUtc) < new Date();

  return (
    <div className="page page-narrow stack-lg">
      <div className="page-head">
        <div>
          <h1>Your booking</h1>
          <p className="page-sub">at {business.name}</p>
        </div>
        <span className={`tag ${cancelled ? 'tag-bad' : 'tag-ok'}`}>{STATUS_LABELS[appointment.status]}</span>
      </div>

      <div className="summary">
        <div className="summary-row">
          <span>Service</span>
          <span>{appointment.serviceName}</span>
        </div>
        <div className="summary-row">
          <span>With</span>
          <span>{appointment.staffDisplayName}</span>
        </div>
        <div className="summary-row">
          <span>When</span>
          <span>{formatInZone(appointment.startsAtUtc, business.timeZoneId)}</span>
        </div>
        <div className="summary-row">
          <span>Price</span>
          <span>{formatMoney(appointment.priceMinorUnits, appointment.currency)}</span>
        </div>
        <div className="summary-row">
          <span>Name</span>
          <span>{appointment.customerName}</span>
        </div>
      </div>

      {error ? <p className="notice notice-error">{error}</p> : null}

      {cancelled ? (
        <p className="notice">This booking is cancelled. You are welcome to book another time.</p>
      ) : past ? (
        <p className="notice">This appointment has already taken place.</p>
      ) : (
        <div className="row">
          <button type="button" className="btn-danger" onClick={cancel} disabled={busy}>
            {busy ? 'Cancelling…' : 'Cancel this booking'}
          </button>
        </div>
      )}
    </div>
  );
}
