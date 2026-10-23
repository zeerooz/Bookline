import { useCallback, useEffect, useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';

import { api } from '../api/client';
import {
  formatDuration,
  formatInZone,
  formatMoney,
  type AvailabilityResponse,
  type BookingConfirmation,
  type ServiceResponse,
  type TenantSummary,
} from '../api/types';

type Step = 'service' | 'time' | 'details' | 'done';

/** How many days of availability to offer at once. */
const DAY_WINDOW = 14;

export function BookPage() {
  const { slug = '' } = useParams();

  const [business, setBusiness] = useState<TenantSummary | null>(null);
  const [services, setServices] = useState<ServiceResponse[]>([]);
  const [loadError, setLoadError] = useState<string | null>(null);

  const [service, setService] = useState<ServiceResponse | null>(null);
  const [day, setDay] = useState<string>(() => isoDate(new Date()));
  const [availability, setAvailability] = useState<AvailabilityResponse[]>([]);
  const [loadingSlots, setLoadingSlots] = useState(false);
  const [slot, setSlot] = useState<{ startsAtUtc: string; staffMemberId: string; staffName: string } | null>(null);

  const [name, setName] = useState('');
  const [email, setEmail] = useState('');
  const [phone, setPhone] = useState('');
  const [notes, setNotes] = useState('');

  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [confirmation, setConfirmation] = useState<BookingConfirmation | null>(null);

  useEffect(() => {
    Promise.all([api.publicBusiness(slug), api.publicServices(slug)])
      .then(([tenant, list]) => {
        setBusiness(tenant);
        setServices(list);
      })
      .catch((cause: unknown) =>
        setLoadError(cause instanceof Error ? cause.message : 'This business could not be loaded.'),
      );
  }, [slug]);

  const loadSlots = useCallback(async () => {
    if (!service) {
      return;
    }

    setLoadingSlots(true);
    setError(null);

    try {
      setAvailability(await api.availability(slug, service.id, day, day));
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Could not load times.');
      setAvailability([]);
    } finally {
      setLoadingSlots(false);
    }
  }, [slug, service, day]);

  useEffect(() => {
    void loadSlots();
  }, [loadSlots]);

  const days = useMemo(() => {
    const today = new Date();
    return Array.from({ length: DAY_WINDOW }, (_, i) => {
      const date = new Date(today);
      date.setDate(today.getDate() + i);
      return date;
    });
  }, []);

  /** Slots from every qualified staff member, flattened and sorted. */
  const slots = useMemo(() => {
    const flattened = availability.flatMap((entry) =>
      entry.slots.map((startsAtUtc) => ({
        startsAtUtc,
        staffMemberId: entry.staffMemberId,
        staffName: entry.staffDisplayName,
      })),
    );

    // Two people free at the same minute would otherwise show the time twice.
    const seen = new Map<string, (typeof flattened)[number]>();

    for (const entry of flattened) {
      if (!seen.has(entry.startsAtUtc)) {
        seen.set(entry.startsAtUtc, entry);
      }
    }

    return [...seen.values()].sort((a, b) => a.startsAtUtc.localeCompare(b.startsAtUtc));
  }, [availability]);

  const step: Step = confirmation ? 'done' : !service ? 'service' : !slot ? 'time' : 'details';

  async function submit(event: React.FormEvent) {
    event.preventDefault();

    if (!service || !slot) {
      return;
    }

    setSubmitting(true);
    setError(null);

    try {
      const result = await api.book(slug, {
        serviceId: service.id,
        staffMemberId: slot.staffMemberId,
        startsAtUtc: slot.startsAtUtc,
        customerName: name.trim(),
        customerEmail: email.trim(),
        customerPhone: phone.trim() || undefined,
        notes: notes.trim() || undefined,
      });

      setConfirmation(result);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Could not complete the booking.');
      // The slot may have gone while the form was being filled in, so refresh
      // the list rather than leaving a stale time selected.
      setSlot(null);
      await loadSlots();
    } finally {
      setSubmitting(false);
    }
  }

  if (loadError) {
    return (
      <div className="page page-narrow">
        <div className="empty">
          <h1>Business not found</h1>
          <p>{loadError}</p>
        </div>
      </div>
    );
  }

  if (!business) {
    return (
      <div className="page page-narrow">
        <p className="empty">Loading…</p>
      </div>
    );
  }

  if (confirmation) {
    return <Confirmed slug={slug} business={business} confirmation={confirmation} />;
  }

  return (
    <div className="page page-narrow">
      <div className="page-head">
        <div>
          <h1>{business.name}</h1>
          <p className="page-sub">Pick a service and a time. No account needed.</p>
        </div>
      </div>

      <p className="steps">
        <span className={step === 'service' ? 'current' : 'done'}>1. Service</span>
        <span>→</span>
        <span className={step === 'time' ? 'current' : service ? 'done' : ''}>2. Time</span>
        <span>→</span>
        <span className={step === 'details' ? 'current' : ''}>3. Your details</span>
      </p>

      <div className="stack-lg">
        <section className="stack">
          <h2>Choose a service</h2>

          {services.length === 0 ? (
            <p className="notice">This business has not published any services yet.</p>
          ) : (
            <div className="grid">
              {services.map((item) => (
                <button
                  key={item.id}
                  type="button"
                  className={`choice ${service?.id === item.id ? 'selected' : ''}`}
                  onClick={() => {
                    setService(item);
                    setSlot(null);
                  }}
                >
                  <span className="choice-title">{item.name}</span>
                  <span className="choice-meta">
                    {formatDuration(item.durationMinutes)} · {formatMoney(item.priceMinorUnits, business.currency)}
                    {item.requiresPrepayment ? ' · pay now' : ''}
                  </span>
                  {item.description ? <span className="choice-meta">{item.description}</span> : null}
                </button>
              ))}
            </div>
          )}
        </section>

        {service ? (
          <section className="stack">
            <h2>Choose a time</h2>
            <p className="inline-note">Times are shown in {business.timeZoneId.replace('_', ' ')}.</p>

            <div className="day-strip">
              {days.map((date) => {
                const value = isoDate(date);

                return (
                  <button
                    key={value}
                    type="button"
                    className={`day ${day === value ? 'selected' : ''}`}
                    onClick={() => {
                      setDay(value);
                      setSlot(null);
                    }}
                  >
                    {date.toLocaleDateString(undefined, { weekday: 'short' })}
                    <small>{date.toLocaleDateString(undefined, { day: 'numeric', month: 'short' })}</small>
                  </button>
                );
              })}
            </div>

            {loadingSlots ? <p className="inline-note">Checking availability…</p> : null}

            {!loadingSlots && slots.length === 0 ? (
              <p className="notice">Nothing free on this day. Try another.</p>
            ) : null}

            {slots.length > 0 ? (
              <div className="slots">
                {slots.map((entry) => (
                  <button
                    key={entry.startsAtUtc}
                    type="button"
                    className={`slot ${slot?.startsAtUtc === entry.startsAtUtc ? 'selected' : ''}`}
                    onClick={() => setSlot(entry)}
                  >
                    {formatInZone(entry.startsAtUtc, business.timeZoneId, {
                      dateStyle: undefined,
                      timeStyle: 'short',
                    })}
                  </button>
                ))}
              </div>
            ) : null}
          </section>
        ) : null}

        {service && slot ? (
          <section className="stack">
            <h2>Your details</h2>

            <div className="summary">
              <div className="summary-row">
                <span>Service</span>
                <span>{service.name}</span>
              </div>
              <div className="summary-row">
                <span>With</span>
                <span>{slot.staffName}</span>
              </div>
              <div className="summary-row">
                <span>When</span>
                <span>{formatInZone(slot.startsAtUtc, business.timeZoneId)}</span>
              </div>
              <div className="summary-row">
                <span>Price</span>
                <span>{formatMoney(service.priceMinorUnits, business.currency)}</span>
              </div>
            </div>

            <form className="stack" onSubmit={submit}>
              <label className="field">
                <span>Full name</span>
                <input type="text" value={name} onChange={(e) => setName(e.target.value)} required autoFocus />
              </label>

              <label className="field">
                <span>Email</span>
                <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} required />
                <span className="field-hint">Your confirmation and reminder go here.</span>
              </label>

              <label className="field">
                <span>Phone (optional)</span>
                <input type="tel" value={phone} onChange={(e) => setPhone(e.target.value)} />
              </label>

              <label className="field">
                <span>Anything we should know? (optional)</span>
                <textarea value={notes} onChange={(e) => setNotes(e.target.value)} />
              </label>

              {error ? <p className="notice notice-error">{error}</p> : null}

              <div className="row row-end">
                <button type="button" className="btn-secondary" onClick={() => setSlot(null)} disabled={submitting}>
                  Change time
                </button>
                <button type="submit" className="btn-primary" disabled={submitting}>
                  {submitting ? 'Booking…' : 'Confirm booking'}
                </button>
              </div>
            </form>
          </section>
        ) : null}
      </div>
    </div>
  );
}

function Confirmed({
  slug,
  business,
  confirmation,
}: {
  slug: string;
  business: TenantSummary;
  confirmation: BookingConfirmation;
}) {
  const { appointment, managementToken, paymentUrl } = confirmation;
  const manageUrl = `${window.location.origin}/booking/${slug}/${managementToken}`;

  return (
    <div className="page page-narrow stack-lg">
      <div>
        <h1>{paymentUrl ? 'Almost there' : 'You are booked'}</h1>
        <p className="page-sub">
          {paymentUrl
            ? 'Your slot is held while you pay. It is released if payment is not completed.'
            : `We have emailed ${appointment.customerEmail} with the details.`}
        </p>
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
          <span>Where</span>
          <span>{business.name}</span>
        </div>
      </div>

      {paymentUrl ? (
        <a className="btn-primary" href={paymentUrl} style={{ textAlign: 'center', textDecoration: 'none' }}>
          Pay {formatMoney(appointment.priceMinorUnits, appointment.currency)}
        </a>
      ) : null}

      <div className="stack">
        <h2>Need to change it?</h2>
        <p className="panel-note">Keep this link. It is the only way to manage this booking without an account.</p>
        <p className="link-box">{manageUrl}</p>
        <div className="row">
          <Link className="btn-secondary" to={`/booking/${slug}/${managementToken}`} style={{ textDecoration: 'none' }}>
            Open my booking
          </Link>
        </div>
      </div>
    </div>
  );
}

function isoDate(date: Date): string {
  // Local calendar date, not UTC: "today" means the user's today.
  const year = date.getFullYear();
  const month = `${date.getMonth() + 1}`.padStart(2, '0');
  const day = `${date.getDate()}`.padStart(2, '0');

  return `${year}-${month}-${day}`;
}
