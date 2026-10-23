import { useCallback, useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';

import { api } from '../api/client';
import {
  AppointmentStatus,
  STATUS_LABELS,
  TenantRole,
  formatDuration,
  formatInZone,
  formatMoney,
  type AppointmentResponse,
  type AuditEntryResponse,
  type BusinessHourResponse,
  type MemberResponse,
  type ServiceResponse,
  type StaffMemberResponse,
  type TenantSummary,
} from '../api/types';
import { useAuth } from '../state/useAuth';

type Tab = 'calendar' | 'services' | 'team' | 'hours' | 'activity';

const DAY_NAMES = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];

export function DashboardPage({ notify }: { notify: (message: string) => void }) {
  const { slug = '' } = useParams();
  const { user } = useAuth();

  const [tab, setTab] = useState<Tab>('calendar');
  const [business, setBusiness] = useState<TenantSummary | null>(null);

  const membership = user?.memberships.find((m) => m.tenantSlug === slug);
  const isOwner = membership?.role === TenantRole.Owner;

  useEffect(() => {
    api.publicBusiness(slug).then(setBusiness).catch(() => setBusiness(null));
  }, [slug]);

  if (!membership) {
    return (
      <div className="page">
        <div className="empty">
          <h1>No access</h1>
          <p>You are not a member of this business.</p>
          <Link to="/app">Back to your businesses</Link>
        </div>
      </div>
    );
  }

  const tabs: { id: Tab; label: string; ownerOnly?: boolean }[] = [
    { id: 'calendar', label: 'Calendar' },
    { id: 'services', label: 'Services' },
    { id: 'team', label: 'Team' },
    { id: 'hours', label: 'Opening hours' },
    { id: 'activity', label: 'Activity', ownerOnly: true },
  ];

  return (
    <div className="page stack-lg">
      <div className="page-head">
        <div>
          <h1>{membership.tenantName}</h1>
          <p className="page-sub">
            Public booking page: <Link to={`/book/${slug}`}>/book/{slug}</Link>
          </p>
        </div>
      </div>

      <nav className="topbar-nav">
        {tabs
          .filter((item) => !item.ownerOnly || isOwner)
          .map((item) => (
            <button
              key={item.id}
              type="button"
              className={`topbar-link ${tab === item.id ? 'active' : ''}`}
              onClick={() => setTab(item.id)}
            >
              {item.label}
            </button>
          ))}
      </nav>

      {tab === 'calendar' ? <CalendarTab slug={slug} business={business} notify={notify} /> : null}
      {tab === 'services' ? <ServicesTab slug={slug} business={business} isOwner={isOwner} notify={notify} /> : null}
      {tab === 'team' ? <TeamTab slug={slug} isOwner={isOwner} notify={notify} /> : null}
      {tab === 'hours' ? <HoursTab slug={slug} isOwner={isOwner} notify={notify} /> : null}
      {tab === 'activity' && isOwner ? <ActivityTab slug={slug} /> : null}
    </div>
  );
}

// ---------------------------------------------------------------- calendar

function CalendarTab({
  slug,
  business,
  notify,
}: {
  slug: string;
  business: TenantSummary | null;
  notify: (message: string) => void;
}) {
  const [appointments, setAppointments] = useState<AppointmentResponse[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  const load = useCallback(async () => {
    setLoading(true);

    try {
      const from = new Date();
      from.setHours(0, 0, 0, 0);

      const to = new Date(from);
      to.setDate(to.getDate() + 30);

      setAppointments(await api.appointments(slug, from.toISOString(), to.toISOString()));
      setError(null);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Could not load the calendar.');
    } finally {
      setLoading(false);
    }
  }, [slug]);

  useEffect(() => {
    void load();
  }, [load]);

  async function act(action: 'cancel' | 'complete' | 'no-show', appointment: AppointmentResponse) {
    if (action === 'cancel' && !window.confirm(`Cancel ${appointment.customerName}'s appointment?`)) {
      return;
    }

    try {
      if (action === 'cancel') {
        await api.cancelAppointment(slug, appointment.id);
        notify('Appointment cancelled');
      } else if (action === 'complete') {
        await api.completeAppointment(slug, appointment.id);
        notify('Marked as completed');
      } else {
        await api.noShowAppointment(slug, appointment.id);
        notify('Marked as a no show');
      }

      await load();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'That did not work.');
    }
  }

  if (loading) {
    return <p className="empty">Loading…</p>;
  }

  return (
    <section className="stack">
      <div className="spread">
        <h2>Next 30 days</h2>
        <button type="button" className="btn-secondary" onClick={() => void load()}>
          Refresh
        </button>
      </div>

      {error ? <p className="notice notice-error">{error}</p> : null}

      {appointments.length === 0 ? (
        <div className="empty">
          <p>Nothing booked yet.</p>
          <p className="inline-note">Share your booking link and appointments will appear here.</p>
        </div>
      ) : (
        <div className="table-scroll">
          <table className="records">
            <thead>
              <tr>
                <th>When</th>
                <th>Customer</th>
                <th>Service</th>
                <th>With</th>
                <th>Status</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {appointments.map((appointment) => (
                <tr key={appointment.id}>
                  <td className="primary">
                    {business ? formatInZone(appointment.startsAtUtc, business.timeZoneId) : appointment.startsAtUtc}
                  </td>
                  <td className="wrap">
                    <span className="primary">{appointment.customerName}</span>
                    <br />
                    <span className="inline-note">{appointment.customerEmail}</span>
                  </td>
                  <td>{appointment.serviceName}</td>
                  <td>{appointment.staffDisplayName}</td>
                  <td>
                    <span className={`tag ${statusTone(appointment.status)}`}>
                      {STATUS_LABELS[appointment.status]}
                    </span>
                  </td>
                  <td>
                    {appointment.status === AppointmentStatus.Confirmed ? (
                      <div className="row">
                        <button type="button" className="btn-ghost" onClick={() => act('complete', appointment)}>
                          Done
                        </button>
                        <button type="button" className="btn-ghost" onClick={() => act('no-show', appointment)}>
                          No show
                        </button>
                        <button type="button" className="btn-ghost" onClick={() => act('cancel', appointment)}>
                          Cancel
                        </button>
                      </div>
                    ) : null}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

function statusTone(status: AppointmentStatus): string {
  if (status === AppointmentStatus.Cancelled || status === AppointmentStatus.NoShow) return 'tag-bad';
  if (status === AppointmentStatus.PendingPayment) return 'tag-warn';
  return 'tag-ok';
}

// ---------------------------------------------------------------- services

function ServicesTab({
  slug,
  business,
  isOwner,
  notify,
}: {
  slug: string;
  business: TenantSummary | null;
  isOwner: boolean;
  notify: (message: string) => void;
}) {
  const [services, setServices] = useState<ServiceResponse[]>([]);
  const [staff, setStaff] = useState<StaffMemberResponse[]>([]);
  const [editing, setEditing] = useState<ServiceResponse | 'new' | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    const [serviceList, staffList] = await Promise.all([api.services(slug), api.staff(slug)]);
    setServices(serviceList);
    setStaff(staffList);
  }, [slug]);

  useEffect(() => {
    load().catch((cause: unknown) =>
      setError(cause instanceof Error ? cause.message : 'Could not load services.'),
    );
  }, [load]);

  return (
    <section className="stack">
      <div className="spread">
        <h2>Services</h2>
        {isOwner ? (
          <button type="button" className="btn-primary" onClick={() => setEditing('new')}>
            Add a service
          </button>
        ) : null}
      </div>

      {error ? <p className="notice notice-error">{error}</p> : null}

      {editing ? (
        <ServiceForm
          slug={slug}
          staff={staff}
          initial={editing === 'new' ? null : editing}
          onDone={async (message) => {
            setEditing(null);
            notify(message);
            await load();
          }}
          onCancel={() => setEditing(null)}
        />
      ) : null}

      {services.length === 0 ? (
        <p className="notice">No services yet. Customers cannot book until there is at least one.</p>
      ) : (
        <div className="grid">
          {services.map((service) => (
            <div key={service.id} className="panel stack">
              <div className="spread">
                <h3>{service.name}</h3>
                {!service.isActive ? <span className="tag">Hidden</span> : null}
              </div>

              <p className="panel-note">
                {formatDuration(service.durationMinutes)}
                {service.bufferMinutes > 0 ? ` + ${service.bufferMinutes} min buffer` : ''} ·{' '}
                {business ? formatMoney(service.priceMinorUnits, business.currency) : service.priceMinorUnits}
                {service.requiresPrepayment ? ' · prepaid' : ''}
              </p>

              {service.description ? <p className="panel-note">{service.description}</p> : null}

              {isOwner ? (
                <div className="row">
                  <button type="button" className="btn-secondary" onClick={() => setEditing(service)}>
                    Edit
                  </button>
                  {service.isActive ? (
                    <button
                      type="button"
                      className="btn-danger"
                      onClick={async () => {
                        await api.deactivateService(slug, service.id);
                        notify('Service hidden');
                        await load();
                      }}
                    >
                      Hide
                    </button>
                  ) : null}
                </div>
              ) : null}
            </div>
          ))}
        </div>
      )}
    </section>
  );
}

function ServiceForm({
  slug,
  staff,
  initial,
  onDone,
  onCancel,
}: {
  slug: string;
  staff: StaffMemberResponse[];
  initial: ServiceResponse | null;
  onDone: (message: string) => Promise<void>;
  onCancel: () => void;
}) {
  const [name, setName] = useState(initial?.name ?? '');
  const [description, setDescription] = useState(initial?.description ?? '');
  const [duration, setDuration] = useState(initial?.durationMinutes ?? 30);
  const [buffer, setBuffer] = useState(initial?.bufferMinutes ?? 0);
  const [price, setPrice] = useState((initial?.priceMinorUnits ?? 0) / 100);
  const [prepay, setPrepay] = useState(initial?.requiresPrepayment ?? false);
  const [selected, setSelected] = useState<string[]>(
    initial ? staff.filter((s) => s.serviceIds.includes(initial.id)).map((s) => s.id) : [],
  );
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);

    const payload = {
      name: name.trim(),
      description: description.trim() || null,
      durationMinutes: duration,
      bufferMinutes: buffer,
      // Back to minor units: money travels as integers, never as a float.
      priceMinorUnits: Math.round(price * 100),
      requiresPrepayment: prepay,
      isActive: true,
      staffMemberIds: selected,
    };

    try {
      if (initial) {
        await api.updateService(slug, initial.id, payload);
        await onDone('Service updated');
      } else {
        await api.createService(slug, payload);
        await onDone('Service added');
      }
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Could not save the service.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="panel stack" onSubmit={submit}>
      <h3>{initial ? 'Edit service' : 'New service'}</h3>

      <label className="field">
        <span>Name</span>
        <input type="text" value={name} onChange={(e) => setName(e.target.value)} required autoFocus />
      </label>

      <label className="field">
        <span>Description (optional)</span>
        <textarea value={description} onChange={(e) => setDescription(e.target.value)} />
      </label>

      <div className="grid">
        <label className="field">
          <span>Duration (minutes)</span>
          <input
            type="number"
            min={5}
            max={1440}
            step={5}
            value={duration}
            onChange={(e) => setDuration(Number(e.target.value))}
          />
        </label>

        <label className="field">
          <span>Buffer after (minutes)</span>
          <input
            type="number"
            min={0}
            max={480}
            step={5}
            value={buffer}
            onChange={(e) => setBuffer(Number(e.target.value))}
          />
          <span className="field-hint">Time blocked after the appointment for clean-up or notes.</span>
        </label>

        <label className="field">
          <span>Price</span>
          <input type="number" min={0} step="0.01" value={price} onChange={(e) => setPrice(Number(e.target.value))} />
        </label>
      </div>

      <label className="checkline">
        <input type="checkbox" checked={prepay} onChange={(e) => setPrepay(e.target.checked)} />
        Require payment before the slot is confirmed
      </label>

      <div className="stack">
        <span className="field-hint">Who can deliver this service?</span>
        {staff.length === 0 ? (
          <p className="notice">Add a team member first — nobody can book a service with no one to deliver it.</p>
        ) : (
          staff.map((member) => (
            <label key={member.id} className="checkline">
              <input
                type="checkbox"
                checked={selected.includes(member.id)}
                onChange={(e) =>
                  setSelected((current) =>
                    e.target.checked ? [...current, member.id] : current.filter((id) => id !== member.id),
                  )
                }
              />
              {member.displayName}
            </label>
          ))
        )}
      </div>

      {error ? <p className="notice notice-error">{error}</p> : null}

      <div className="row row-end">
        <button type="button" className="btn-secondary" onClick={onCancel} disabled={busy}>
          Cancel
        </button>
        <button type="submit" className="btn-primary" disabled={busy}>
          {busy ? 'Saving…' : 'Save service'}
        </button>
      </div>
    </form>
  );
}

// -------------------------------------------------------------------- team

function TeamTab({
  slug,
  isOwner,
  notify,
}: {
  slug: string;
  isOwner: boolean;
  notify: (message: string) => void;
}) {
  const [staff, setStaff] = useState<StaffMemberResponse[]>([]);
  const [members, setMembers] = useState<MemberResponse[]>([]);
  const [displayName, setDisplayName] = useState('');
  const [title, setTitle] = useState('');
  const [inviteEmail, setInviteEmail] = useState('');
  const [inviteRole, setInviteRole] = useState<number>(TenantRole.Staff);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setStaff(await api.staff(slug));

    if (isOwner) {
      setMembers(await api.members(slug));
    }
  }, [slug, isOwner]);

  useEffect(() => {
    load().catch((cause: unknown) => setError(cause instanceof Error ? cause.message : 'Could not load the team.'));
  }, [load]);

  async function addStaff(event: React.FormEvent) {
    event.preventDefault();

    try {
      await api.createStaff(slug, { displayName: displayName.trim(), title: title.trim() || null, serviceIds: [] });
      setDisplayName('');
      setTitle('');
      notify('Team member added');
      await load();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Could not add that person.');
    }
  }

  async function invite(event: React.FormEvent) {
    event.preventDefault();

    try {
      await api.addMember(slug, { email: inviteEmail.trim(), role: inviteRole as TenantRole });
      setInviteEmail('');
      notify('Account linked to this business');
      await load();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Could not add that account.');
    }
  }

  return (
    <section className="stack-lg">
      {error ? <p className="notice notice-error">{error}</p> : null}

      <section className="stack">
        <h2>Who delivers services</h2>
        <p className="panel-note">
          These names appear on your booking page. A person here does not need an account; add one below if they
          should be able to sign in.
        </p>

        {staff.length === 0 ? <p className="notice">Nobody added yet.</p> : null}

        <div className="grid">
          {staff.map((member) => (
            <div key={member.id} className="panel stack">
              <div className="spread">
                <h3>{member.displayName}</h3>
                {!member.isActive ? <span className="tag">Inactive</span> : null}
              </div>
              {member.title ? <p className="panel-note">{member.title}</p> : null}
              <p className="inline-note">{member.serviceIds.length} service(s)</p>

              {isOwner && member.isActive ? (
                <div className="row">
                  <button
                    type="button"
                    className="btn-danger"
                    onClick={async () => {
                      await api.deactivateStaff(slug, member.id);
                      notify('Team member deactivated');
                      await load();
                    }}
                  >
                    Deactivate
                  </button>
                </div>
              ) : null}
            </div>
          ))}
        </div>

        {isOwner ? (
          <form className="panel row" onSubmit={addStaff}>
            <label className="field" style={{ flex: '1 1 180px' }}>
              <span>Name</span>
              <input type="text" value={displayName} onChange={(e) => setDisplayName(e.target.value)} required />
            </label>
            <label className="field" style={{ flex: '1 1 180px' }}>
              <span>Title (optional)</span>
              <input type="text" value={title} onChange={(e) => setTitle(e.target.value)} />
            </label>
            <button type="submit" className="btn-primary">
              Add
            </button>
          </form>
        ) : null}
      </section>

      {isOwner ? (
        <section className="stack">
          <h2>Who can sign in</h2>
          <p className="panel-note">
            There is no email invitation yet, so the person must already have an account. Removing someone takes
            effect on their very next request.
          </p>

          <div className="table-scroll">
            <table className="records">
              <thead>
                <tr>
                  <th>Person</th>
                  <th>Role</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {members.map((member) => (
                  <tr key={member.userId}>
                    <td className="primary wrap">
                      {member.fullName}
                      <br />
                      <span className="inline-note">{member.email}</span>
                    </td>
                    <td>{member.role === TenantRole.Owner ? 'Owner' : member.role === TenantRole.Staff ? 'Staff' : 'Customer'}</td>
                    <td>
                      <button
                        type="button"
                        className="btn-ghost"
                        onClick={async () => {
                          try {
                            await api.removeMember(slug, member.userId);
                            notify('Access removed');
                            await load();
                          } catch (cause) {
                            setError(cause instanceof Error ? cause.message : 'Could not remove that person.');
                          }
                        }}
                      >
                        Remove
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          <form className="panel row" onSubmit={invite}>
            <label className="field" style={{ flex: '1 1 220px' }}>
              <span>Email of an existing account</span>
              <input type="email" value={inviteEmail} onChange={(e) => setInviteEmail(e.target.value)} required />
            </label>
            <label className="field" style={{ flex: '0 1 140px' }}>
              <span>Role</span>
              <select value={inviteRole} onChange={(e) => setInviteRole(Number(e.target.value))}>
                <option value={TenantRole.Staff}>Staff</option>
                <option value={TenantRole.Owner}>Owner</option>
              </select>
            </label>
            <button type="submit" className="btn-primary">
              Give access
            </button>
          </form>
        </section>
      ) : null}
    </section>
  );
}

// ------------------------------------------------------------------- hours

function HoursTab({
  slug,
  isOwner,
  notify,
}: {
  slug: string;
  isOwner: boolean;
  notify: (message: string) => void;
}) {
  const [hours, setHours] = useState<BusinessHourResponse[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api
      .hours(slug)
      .then(setHours)
      .catch((cause: unknown) => setError(cause instanceof Error ? cause.message : 'Could not load hours.'));
  }, [slug]);

  function windowFor(day: number) {
    return hours.find((h) => h.dayOfWeek === day && h.staffMemberId === null);
  }

  function setDay(day: number, open: boolean, startMinute: number, endMinute: number) {
    setHours((current) => {
      const others = current.filter((h) => !(h.dayOfWeek === day && h.staffMemberId === null));

      if (!open) {
        return others;
      }

      return [...others, { id: `${day}`, dayOfWeek: day, startMinute, endMinute, staffMemberId: null }];
    });
  }

  async function save() {
    setBusy(true);
    setError(null);

    try {
      // The whole timetable is replaced in one call, so there is no
      // half-saved state if something goes wrong partway.
      const payload = hours.map((h) => ({
        dayOfWeek: h.dayOfWeek,
        startMinute: h.startMinute,
        endMinute: h.endMinute,
        staffMemberId: h.staffMemberId,
      }));

      setHours(await api.replaceHours(slug, payload));
      notify('Opening hours saved');
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Could not save the hours.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="stack">
      <h2>Opening hours</h2>
      <p className="panel-note">
        Bookings are only offered inside these windows. Times are in the business time zone, so they stay correct
        across daylight-saving changes.
      </p>

      {error ? <p className="notice notice-error">{error}</p> : null}

      <div className="stack">
        {DAY_NAMES.map((label, day) => {
          const window = windowFor(day);
          const open = window !== undefined;

          return (
            <div key={label} className="panel row">
              <label className="checkline" style={{ flex: '0 0 150px' }}>
                <input
                  type="checkbox"
                  checked={open}
                  disabled={!isOwner}
                  onChange={(e) => setDay(day, e.target.checked, 9 * 60, 17 * 60)}
                />
                {label}
              </label>

              {open ? (
                <>
                  <label className="field" style={{ flex: '0 1 130px' }}>
                    <span>Opens</span>
                    <input
                      type="time"
                      value={minutesToTime(window.startMinute)}
                      disabled={!isOwner}
                      onChange={(e) => setDay(day, true, timeToMinutes(e.target.value), window.endMinute)}
                    />
                  </label>
                  <label className="field" style={{ flex: '0 1 130px' }}>
                    <span>Closes</span>
                    <input
                      type="time"
                      value={minutesToTime(window.endMinute)}
                      disabled={!isOwner}
                      onChange={(e) => setDay(day, true, window.startMinute, timeToMinutes(e.target.value))}
                    />
                  </label>
                </>
              ) : (
                <span className="inline-note">Closed</span>
              )}
            </div>
          );
        })}
      </div>

      {isOwner ? (
        <div className="row row-end">
          <button type="button" className="btn-primary" onClick={() => void save()} disabled={busy}>
            {busy ? 'Saving…' : 'Save hours'}
          </button>
        </div>
      ) : null}
    </section>
  );
}

function minutesToTime(minutes: number): string {
  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;

  return `${`${hours}`.padStart(2, '0')}:${`${rest}`.padStart(2, '0')}`;
}

function timeToMinutes(value: string): number {
  const [hours, minutes] = value.split(':').map(Number);
  return (hours || 0) * 60 + (minutes || 0);
}

// ---------------------------------------------------------------- activity

function ActivityTab({ slug }: { slug: string }) {
  const [entries, setEntries] = useState<AuditEntryResponse[]>([]);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api
      .audit(slug)
      .then(setEntries)
      .catch((cause: unknown) => setError(cause instanceof Error ? cause.message : 'Could not load activity.'));
  }, [slug]);

  return (
    <section className="stack">
      <h2>Activity</h2>
      <p className="panel-note">Every change anyone made to this business, and who made it.</p>

      {error ? <p className="notice notice-error">{error}</p> : null}

      <div className="table-scroll">
        <table className="records">
          <thead>
            <tr>
              <th>What</th>
              <th>Detail</th>
              <th>Who</th>
              <th>When</th>
            </tr>
          </thead>
          <tbody>
            {entries.map((entry) => (
              <tr key={entry.id}>
                <td className="primary">{entry.action}</td>
                <td className="wrap">{entry.detail ?? '—'}</td>
                <td className="wrap">{entry.actorEmail ?? 'system'}</td>
                <td>{new Date(entry.createdAt).toLocaleString()}</td>
              </tr>
            ))}
            {entries.length === 0 ? (
              <tr>
                <td colSpan={4}>Nothing recorded yet.</td>
              </tr>
            ) : null}
          </tbody>
        </table>
      </div>
    </section>
  );
}
