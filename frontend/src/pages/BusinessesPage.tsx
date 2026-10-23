import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';

import { api } from '../api/client';
import { TenantRole } from '../api/types';
import { useAuth } from '../state/useAuth';

const ROLE_LABELS: Record<number, string> = {
  [TenantRole.Customer]: 'Customer',
  [TenantRole.Staff]: 'Staff',
  [TenantRole.Owner]: 'Owner',
};

export function BusinessesPage() {
  const { user, refresh } = useAuth();
  const navigate = useNavigate();

  const [creating, setCreating] = useState(false);
  const [name, setName] = useState('');
  const [slug, setSlug] = useState('');
  const [timeZoneId, setTimeZoneId] = useState('Africa/Cairo');
  const [currency, setCurrency] = useState('EGP');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function create(event: React.FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);

    try {
      const created = await api.createBusiness({
        name: name.trim(),
        slug: slug.trim().toLowerCase(),
        timeZoneId,
        currency,
        minimumNoticeMinutes: 60,
        bookingHorizonDays: 60,
      });

      await refresh();
      navigate(`/app/${created.slug}`);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Could not create the business.');
    } finally {
      setBusy(false);
    }
  }

  const memberships = user?.memberships ?? [];

  return (
    <div className="page stack-lg">
      <div className="page-head">
        <div>
          <h1>Your businesses</h1>
          <p className="page-sub">Each business has its own calendar, team and booking page.</p>
        </div>
        {!creating ? (
          <button type="button" className="btn-primary" onClick={() => setCreating(true)}>
            Add a business
          </button>
        ) : null}
      </div>

      {creating ? (
        <section className="panel">
          <h2>New business</h2>
          <p className="panel-note">The address becomes your public booking link, so pick something readable.</p>

          <form className="stack" onSubmit={create} style={{ marginTop: 14, maxWidth: 420 }}>
            <label className="field">
              <span>Name</span>
              <input
                type="text"
                value={name}
                onChange={(e) => {
                  setName(e.target.value);
                  // Suggest a slug, but let it be overridden.
                  if (!slug || slug === suggestSlug(name)) {
                    setSlug(suggestSlug(e.target.value));
                  }
                }}
                required
                autoFocus
              />
            </label>

            <label className="field">
              <span>Booking address</span>
              <input
                type="text"
                value={slug}
                onChange={(e) => setSlug(e.target.value)}
                pattern="[a-z0-9]+(-[a-z0-9]+)*"
                required
              />
              <span className="field-hint">/book/{slug || 'your-business'}</span>
            </label>

            <label className="field">
              <span>Time zone</span>
              <select value={timeZoneId} onChange={(e) => setTimeZoneId(e.target.value)}>
                <option value="Africa/Cairo">Africa/Cairo</option>
                <option value="Europe/London">Europe/London</option>
                <option value="Europe/Berlin">Europe/Berlin</option>
                <option value="Asia/Dubai">Asia/Dubai</option>
                <option value="Asia/Riyadh">Asia/Riyadh</option>
                <option value="UTC">UTC</option>
              </select>
              <span className="field-hint">All opening hours and bookings are shown in this zone.</span>
            </label>

            <label className="field">
              <span>Currency</span>
              <select value={currency} onChange={(e) => setCurrency(e.target.value)}>
                <option value="EGP">EGP</option>
                <option value="USD">USD</option>
                <option value="EUR">EUR</option>
                <option value="GBP">GBP</option>
                <option value="AED">AED</option>
                <option value="SAR">SAR</option>
              </select>
            </label>

            {error ? <p className="notice notice-error">{error}</p> : null}

            <div className="row row-end">
              <button type="button" className="btn-secondary" onClick={() => setCreating(false)} disabled={busy}>
                Cancel
              </button>
              <button type="submit" className="btn-primary" disabled={busy}>
                {busy ? 'Creating…' : 'Create business'}
              </button>
            </div>
          </form>
        </section>
      ) : null}

      {memberships.length === 0 && !creating ? (
        <div className="empty">
          <p>You are not part of any business yet.</p>
          <button type="button" className="btn-primary" onClick={() => setCreating(true)}>
            Create your first one
          </button>
        </div>
      ) : null}

      {memberships.length > 0 ? (
        <div className="grid">
          {memberships.map((membership) => (
            <Link
              key={membership.tenantId}
              to={`/app/${membership.tenantSlug}`}
              className="choice"
              style={{ textDecoration: 'none' }}
            >
              <span className="choice-title">{membership.tenantName}</span>
              <span className="choice-meta">
                /book/{membership.tenantSlug} · {ROLE_LABELS[membership.role]}
              </span>
            </Link>
          ))}
        </div>
      ) : null}
    </div>
  );
}

function suggestSlug(value: string): string {
  return value
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 48);
}
