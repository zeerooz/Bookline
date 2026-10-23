#!/usr/bin/env node
/*
 * Seeds two businesses with staff, services, opening hours and a few bookings.
 *
 * Two of them on purpose: a single-tenant demo proves nothing about a
 * multi-tenant product. With two in place a reviewer can sign in as one owner
 * and watch the other business stay completely invisible.
 *
 * Everything goes through the public API, so this also serves as an
 * end-to-end check of registration, authorization, and the booking engine.
 *
 * Usage:
 *   node tools/seed-demo.mjs [--api http://localhost:8081]
 */

const args = parseArgs(process.argv.slice(2));
const API = args.api ?? process.env.SEED_API_URL ?? 'http://localhost:8081';
const PASSWORD = 'demo-password-2026';

const BUSINESSES = [
  {
    slug: 'bright-smile-clinic',
    name: 'Bright Smile Clinic',
    owner: 'owner@brightsmile.test',
    timeZoneId: 'Africa/Cairo',
    currency: 'EGP',
    staff: [
      { displayName: 'Dr Mona Fahmy', title: 'Dentist' },
      { displayName: 'Dr Karim Saad', title: 'Orthodontist' },
    ],
    services: [
      { name: 'Check-up and clean', durationMinutes: 30, bufferMinutes: 10, price: 45000, staff: [0, 1] },
      { name: 'Filling', durationMinutes: 45, bufferMinutes: 15, price: 90000, staff: [0] },
      { name: 'Orthodontic consultation', durationMinutes: 60, price: 60000, staff: [1], prepay: true },
    ],
    hours: { days: [0, 1, 2, 3, 4, 6], open: 9 * 60, close: 17 * 60 },
  },
  {
    slug: 'nile-barbers',
    name: 'Nile Barbers',
    owner: 'owner@nilebarbers.test',
    timeZoneId: 'Africa/Cairo',
    currency: 'EGP',
    staff: [{ displayName: 'Hassan Ali', title: 'Master barber' }],
    services: [
      { name: 'Haircut', durationMinutes: 30, price: 15000, staff: [0] },
      { name: 'Cut and beard trim', durationMinutes: 45, bufferMinutes: 10, price: 22000, staff: [0] },
    ],
    hours: { days: [1, 2, 3, 4, 5, 6], open: 11 * 60, close: 21 * 60 },
  },
];

main().catch((error) => {
  console.error(`\nSeeding failed: ${error.message}`);
  process.exit(1);
});

async function main() {
  console.log(`Seeding demo data at ${API}`);
  await waitForApi();

  for (const spec of BUSINESSES) {
    await seedBusiness(spec);
  }

  console.log('\nDemo data ready.');
  console.log('\n  Sign in as a business owner:');

  for (const spec of BUSINESSES) {
    console.log(`    ${spec.owner}  /  ${PASSWORD}`);
  }

  console.log('\n  Or book as a customer, with no account at all:');

  for (const spec of BUSINESSES) {
    console.log(`    ${API}/book/${spec.slug}`);
  }
}

async function seedBusiness(spec) {
  console.log(`\n${spec.name}`);

  const token = await registerOrSignIn(spec.owner, spec.name.split(' ')[0] + ' Owner');

  const created = await post('/api/businesses', {
    name: spec.name,
    slug: spec.slug,
    timeZoneId: spec.timeZoneId,
    currency: spec.currency,
    minimumNoticeMinutes: 60,
    bookingHorizonDays: 60,
  }, token);

  if (created.status === 409) {
    console.log('  already seeded, skipping');
    return;
  }

  if (created.status) {
    throw new Error(`creating ${spec.slug} failed (HTTP ${created.status}): ${created.detail}`);
  }

  const staffIds = [];

  for (const member of spec.staff) {
    const staff = await post(`/api/t/${spec.slug}/staff`, { ...member, serviceIds: [] }, token, true);
    staffIds.push(staff.id);
    console.log(`  + ${member.displayName}`);
  }

  const serviceIds = [];

  for (const service of spec.services) {
    const created = await post(`/api/t/${spec.slug}/services`, {
      name: service.name,
      durationMinutes: service.durationMinutes,
      bufferMinutes: service.bufferMinutes ?? 0,
      priceMinorUnits: service.price,
      requiresPrepayment: service.prepay ?? false,
      isActive: true,
      staffMemberIds: service.staff.map((i) => staffIds[i]),
    }, token, true);

    serviceIds.push(created.id);
    console.log(`  + ${service.name}`);
  }

  const hours = spec.hours.days.map((day) => ({
    dayOfWeek: day,
    startMinute: spec.hours.open,
    endMinute: spec.hours.close,
    staffMemberId: null,
  }));

  await put(`/api/t/${spec.slug}/hours`, hours, token);
  console.log(`  + opening hours on ${spec.hours.days.length} days`);

  await seedBookings(spec, serviceIds[0]);
}

/** Books the first few genuinely free slots, so the calendar is not empty. */
async function seedBookings(spec, serviceId) {
  const customers = [
    { name: 'Omar Khaled', email: 'omar@example.test', phone: '+201000000001' },
    { name: 'Layla Mostafa', email: 'layla@example.test', phone: '+201000000002' },
    { name: 'Youssef Nabil', email: 'youssef@example.test', phone: '+201000000003' },
  ];

  let booked = 0;

  for (let dayOffset = 1; dayOffset <= 10 && booked < customers.length; dayOffset++) {
    const date = new Date();
    date.setDate(date.getDate() + dayOffset);
    const day = date.toISOString().slice(0, 10);

    const availability = await get(`/api/public/${spec.slug}/availability?serviceId=${serviceId}&from=${day}&to=${day}`);
    const slots = availability.flatMap((entry) =>
      entry.slots.map((startsAtUtc) => ({ startsAtUtc, staffMemberId: entry.staffMemberId })),
    );

    if (slots.length === 0) {
      continue;
    }

    const slot = slots[Math.min(2, slots.length - 1)];
    const customer = customers[booked];

    const response = await post(`/api/public/${spec.slug}/bookings`, {
      serviceId,
      staffMemberId: slot.staffMemberId,
      startsAtUtc: slot.startsAtUtc,
      customerName: customer.name,
      customerEmail: customer.email,
      customerPhone: customer.phone,
    });

    if (!response.status) {
      booked++;
      console.log(`  + booking for ${customer.name}`);
    }
  }
}

// --- plumbing ---------------------------------------------------------------

async function registerOrSignIn(email, fullName) {
  const registered = await post('/api/auth/register', { email, password: PASSWORD, fullName });

  if (!registered.status) {
    return registered.accessToken;
  }

  const signedIn = await post('/api/auth/login', { email, password: PASSWORD }, null, true);
  return signedIn.accessToken;
}

async function waitForApi() {
  for (let attempt = 0; attempt < 60; attempt++) {
    try {
      const response = await fetch(`${API}/health`);

      if (response.ok) {
        return;
      }
    } catch {
      // Not up yet.
    }

    await new Promise((resolve) => setTimeout(resolve, 1000));
  }

  throw new Error(`${API} did not become healthy in 60 seconds.`);
}

async function post(path, body, token, throwOnError = false) {
  return send('POST', path, body, token, throwOnError);
}

async function put(path, body, token, throwOnError = true) {
  return send('PUT', path, body, token, throwOnError);
}

async function send(method, path, body, token, throwOnError) {
  const response = await fetch(`${API}${path}`, {
    method,
    headers: {
      'Content-Type': 'application/json',
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
    },
    body: JSON.stringify(body),
  });

  if (!response.ok) {
    const problem = await response.json().catch(() => ({}));

    if (throwOnError) {
      throw new Error(`${method} ${path} returned HTTP ${response.status}: ${problem.detail ?? 'unknown error'}`);
    }

    return { status: response.status, detail: problem.detail };
  }

  return response.status === 204 ? {} : response.json();
}

async function get(path) {
  const response = await fetch(`${API}${path}`);

  if (!response.ok) {
    throw new Error(`GET ${path} returned HTTP ${response.status}`);
  }

  return response.json();
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
