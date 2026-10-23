#!/usr/bin/env node
/*
 * Fires many simultaneous bookings at one slot and asserts that exactly one
 * wins.
 *
 * This is the test the SQLite integration suite cannot run. The guarantee
 * comes from a Postgres exclusion constraint, and the race it defends against
 * — two requests both seeing the slot free before either has written — only
 * happens with genuinely parallel writers against the real database.
 *
 * Usage:
 *   node tools/concurrency-check.mjs [--api http://localhost:8081] [--attempts 12]
 */

const args = parseArgs(process.argv.slice(2));
const API = args.api ?? 'http://localhost:8081';
const ATTEMPTS = Number(args.attempts ?? 12);
const PASSWORD = 'concurrency-check-password';

main().catch((error) => {
  console.error(`\nConcurrency check failed: ${error.message}`);
  process.exit(1);
});

async function main() {
  const slug = `race-${Date.now().toString(36)}`;
  console.log(`Setting up ${slug} at ${API}`);

  const token = await signUp(`race-${Date.now()}@example.test`);

  await request('POST', '/api/businesses', {
    name: 'Race Condition Test',
    slug,
    timeZoneId: 'UTC',
    currency: 'EGP',
    minimumNoticeMinutes: 0,
    bookingHorizonDays: 365,
  }, token);

  const staff = await request('POST', `/api/t/${slug}/staff`,
    { displayName: 'Only Staff Member', serviceIds: [] }, token);

  const service = await request('POST', `/api/t/${slug}/services`, {
    name: 'Contested Slot',
    durationMinutes: 60,
    bufferMinutes: 0,
    priceMinorUnits: 1000,
    isActive: true,
    staffMemberIds: [staff.id],
  }, token);

  await request('PUT', `/api/t/${slug}/hours`,
    [0, 1, 2, 3, 4, 5, 6].map((dayOfWeek) => ({
      dayOfWeek,
      startMinute: 0,
      endMinute: 1440,
      staffMemberId: null,
    })), token);

  // A fixed instant well inside the booking horizon.
  const target = new Date();
  target.setUTCDate(target.getUTCDate() + 3);
  target.setUTCHours(10, 0, 0, 0);

  console.log(`Firing ${ATTEMPTS} simultaneous bookings at ${target.toISOString()}`);

  const results = await Promise.all(
    Array.from({ length: ATTEMPTS }, (_, i) =>
      fetch(`${API}/api/public/${slug}/bookings`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          serviceId: service.id,
          staffMemberId: staff.id,
          startsAtUtc: target.toISOString(),
          customerName: `Racer ${i}`,
          customerEmail: `racer-${i}@example.test`,
        }),
      }).then((response) => response.status),
    ),
  );

  const created = results.filter((status) => status === 201).length;
  const conflicted = results.filter((status) => status === 409).length;
  const other = results.filter((status) => status !== 201 && status !== 409);

  console.log(`  created:    ${created}`);
  console.log(`  conflicted: ${conflicted}`);

  if (other.length > 0) {
    throw new Error(`unexpected status codes: ${[...new Set(other)].join(', ')}`);
  }

  if (created !== 1) {
    throw new Error(`expected exactly 1 booking to succeed, but ${created} did — the slot was double booked`);
  }

  // Confirm against the database's own view, not just the response codes.
  const from = new Date(target.getTime() - 3600_000).toISOString();
  const to = new Date(target.getTime() + 3600_000).toISOString();

  const appointments = await request('GET', `/api/t/${slug}/appointments?fromUtc=${from}&toUtc=${to}`, null, token);
  const active = appointments.filter((a) => a.status !== 2);

  if (active.length !== 1) {
    throw new Error(`the calendar holds ${active.length} appointments for a slot that fits one`);
  }

  console.log('\nExactly one booking won the race. The overlap constraint holds.');
}

async function signUp(email) {
  const result = await request('POST', '/api/auth/register', {
    email,
    password: PASSWORD,
    fullName: 'Race Tester',
  });

  return result.accessToken;
}

async function request(method, path, body, token) {
  const response = await fetch(`${API}${path}`, {
    method,
    headers: {
      ...(body ? { 'Content-Type': 'application/json' } : {}),
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
    },
    body: body ? JSON.stringify(body) : undefined,
  });

  if (!response.ok) {
    const problem = await response.json().catch(() => ({}));
    throw new Error(`${method} ${path} returned HTTP ${response.status}: ${problem.detail ?? 'unknown error'}`);
  }

  return response.status === 204 ? {} : response.json();
}

function parseArgs(argv) {
  const parsed = {};

  for (let i = 0; i < argv.length; i += 2) {
    const key = argv[i]?.replace(/^--/, '');

    if (key) {
      parsed[key] = argv[i + 1];
    }
  }

  return parsed;
}
