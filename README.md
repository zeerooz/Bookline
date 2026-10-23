# Bookline

Multi-tenant appointment booking for small businesses and clinics. Each business
gets its own public booking page, calendar, team and opening hours — and can
never see another's data.

```
ASP.NET Core 10  ·  React 19 + TypeScript  ·  PostgreSQL 18  ·  Docker  ·  75 tests
```

---

## Run it

```bash
git clone https://github.com/zeerooz/Bookline.git
cd Bookline

cp .env.example .env
echo "JWT_SIGNING_KEY=$(openssl rand -base64 48)" >> .env

docker compose up -d --build
node tools/seed-demo.mjs
```

Nothing else to configure: payments are simulated and email is written to the
API log, so there are no accounts to create and no keys to paste.

Open <http://localhost:8081>.

| Try this | Where |
|---|---|
| Book as a customer, no account | `/book/bright-smile-clinic` |
| A second, unrelated business | `/book/nile-barbers` |
| Manage the business | sign in as `owner@brightsmile.test` / `demo-password-2026` |
| The other owner, who sees none of the first | `owner@nilebarbers.test` / `demo-password-2026` |

Sign in as one owner and try to open the other business's dashboard. That is the
property the whole product rests on.

## The three problems worth talking about

### 1. Tenant isolation that fails closed

A shared database with a `TenantId` column is the easy part. The hard part is
that **one forgotten `WHERE` clause leaks every customer's data**, and nothing
in the type system stops you writing one.

So the filter is not written per query, or even per entity. Every entity
implementing `ITenantOwned` gets a global query filter applied by reflection
over the model:

```csharp
foreach (var entityType in b.Model.GetEntityTypes())
{
    if (!typeof(ITenantOwned).IsAssignableFrom(entityType.ClrType)) continue;
    // ... e.TenantId == CurrentTenantId
}
```

A new entity is isolated the moment it is mapped — no one has to remember.

And when no tenant is resolved, the filter compares against `Guid.Empty`, so an
unscoped query returns **nothing** rather than everything. Failing closed costs a
confusing empty list; failing open costs the company.

Two places genuinely need to read across tenants — the payment webhook, which
has no tenant in its URL, and the background notification worker. Both call an
explicitly named `BypassForBackgroundWork()`, so the exceptions are greppable
rather than accidental.

### 2. Double booking is prevented by the database, not by hoping

Checking availability and then inserting is **not atomic**. Two customers
clicking the same slot a millisecond apart both see it free.

The application still checks — it gives a good error message. But the guarantee
comes from Postgres:

```sql
ALTER TABLE "Appointments" ADD CONSTRAINT "ck_appointments_no_staff_overlap"
EXCLUDE USING gist (
    "StaffMemberId" WITH =,
    tstzrange("StartsAtUtc", "BlockedUntilUtc", '[)') WITH &&
)
WHERE ("Status" IN (0, 1, 3));
```

The second writer is rejected by the database and the API turns that into "pick
another slot". Serialising every booking would also work, at the cost of
throughput; this is exact and cheap.

CI proves it under real concurrency, because a SQLite test cannot:

```bash
node tools/concurrency-check.mjs
#   created:    1
#   conflicted: 11
# Exactly one booking won the race. The overlap constraint holds.
```

### 3. Time is the part that silently goes wrong

Opening hours are stored as **local wall-clock minutes**, not instants. "We open
at 09:00" has to stay true across a daylight-saving change — and Egypt observes
DST again, so this is not hypothetical.

Converting a local time to an instant has two cases that a subtraction misses:

- **The clocks go forward** and the local time never happens. Offering a slot at
  a time no clock will show is worse than offering one slot fewer, so it is skipped.
- **The clocks go back** and the same local time happens twice. The first
  occurrence wins, so the business opens once rather than appearing to open twice.

The availability engine is a pure function — no clock, no database, no ambient
time zone — which is what makes those cases testable without standing up a
business.

## What it does

- **Public booking page** per business: pick a service, pick a day, pick a time.
  No account required; guests get an unguessable management link to cancel with.
- **Availability** computed from opening hours minus existing appointments,
  per-service buffer time and staff time off.
- **Roles per business** — Owner, Staff, Customer. Staff see only their own
  calendar. The same person can be staff at one business and a customer at
  another, which is why roles live on a membership rather than on the account.
- **Payments** via Stripe Checkout in test mode, behind an `IPaymentProvider`
  interface, with a fake provider so the prepayment path runs with no keys.
  Prepaid slots are held as `PendingPayment` during checkout and released if it
  is abandoned.
- **Email confirmations and reminders** through a transactional outbox, drained
  by a background worker with exponential backoff.
- **Audit log** of every change, scoped per business.

## Architecture notes a reviewer might ask about

**Roles are never in the token.** The JWT carries a user id and nothing else.
Every request reads the membership row, so removing someone takes effect
immediately instead of whenever their token expires. There is a test for exactly
that, using an already-issued token.

**Money is integers in minor units.** Prices are piastres, never `decimal` and
certainly never `double`. The appointment snapshots the price at booking time,
because re-reading the service later would silently rewrite history every time a
price changes.

**Deactivate, don't delete.** Services and staff are deactivated, because past
appointments reference them and "what did we earn on check-ups last year" has to
keep working.

**404 rather than 403 for another tenant's record.** A 403 confirms the id
exists, which is information the caller is not entitled to.

## Deploying it

A [Render blueprint](render.yaml) is included. Push the repo, then in Render
choose **New → Blueprint** and point it at it. The database, the service and the
JWT signing key are all created for you; there is nothing to paste in.

The root [`Dockerfile`](Dockerfile) builds a **single image** in which the API
also serves the built frontend from `wwwroot`. That is deliberate and different
from `docker compose`, which runs the API and nginx separately:

| | compose | single image |
|---|---|---|
| Processes | API + nginx + Postgres | one API process |
| Origins | one, via the nginx proxy | one, inherently |
| Why | closer to a real deployment | platforms bill per service |

Running them as two services on a hosting platform would mean a second URL,
cross-origin configuration, and a circular reference in the blueprint — each
service needing the other's address before either exists. One service avoids
all of it.

Two details that only show up once it is behind a proxy, both handled:

- **`DATABASE_URL`** arrives as `postgres://user:pass@host/db`, which Npgsql
  does not accept. It is translated in `Data/DatabaseUrl.cs`, with the SSL mode
  negotiated rather than forced, so the same build works against a managed
  database and a local container.
- **Forwarded headers** are honoured, because otherwise every request appears
  to come from the proxy — which would put all users in a single rate-limit
  bucket and record the wrong address in the audit log.

## Tests

```bash
cd backend  && dotnet test   # 67 tests
cd frontend && npm run test  # 8 tests
```

The integration tests boot the real pipeline — real Identity, real authorization
policies, real tenant middleware — against SQLite. The ones worth reading:

| Test | What it pins down |
|---|---|
| `An_owner_cannot_read_another_businesss_services` | Cross-tenant access is refused |
| `One_businesss_bookings_do_not_block_anothers_calendar` | Isolation works in both directions |
| `Revoking_membership_takes_effect_on_the_next_request` | Roles are not cached in the token |
| `Staff_see_only_their_own_appointments` | Enforced in the API, not the UI |
| `A_slot_that_only_touches_another_is_allowed` | Half-open intervals: 11:00 is free after a 10:00–11:00 booking |
| `A_buffer_blocks_the_slot_that_follows` | Clean-up time occupies the calendar |
| `Replaying_the_same_webhook_changes_nothing` | Providers retry; customers get one email |
| `A_local_time_that_does_not_exist_is_skipped` | Daylight saving, spring forward |
| `A_business_cannot_be_left_without_an_owner` | You cannot lock yourself out |

**One honest caveat**: the integration tests run on SQLite, so the Postgres
exclusion constraint is not exercised there. That gap is covered by
`tools/concurrency-check.mjs`, which CI runs against real Postgres on every push.

## Layout

```
backend/
  src/Bookline.Api/
    Domain/        entities; ITenantOwned is what drives isolation
    Data/          DbContext with the global filters, and migrations
    Services/      availability engine, booking, notifications, payments
    Controllers/   public booking, management, payment webhooks
    Security/      tenant context, per-tenant role policies, rate limits
    Middleware/    tenant resolution from the route
  tests/           67 unit and integration tests
frontend/
  src/pages/       booking flow, manage booking, dashboard
  src/api/         typed client
tools/
  seed-demo.mjs          two businesses, so isolation is visible
  concurrency-check.mjs  proves the overlap constraint on real Postgres
```

## Development

```bash
docker compose up -d db          # Postgres only

cd backend && dotnet run --project src/Bookline.Api    # :5090, docs at /scalar
cd frontend && npm install && npm run dev              # :5173
```

The development signing key is committed in `appsettings.Development.json` on
purpose, so the project runs immediately after cloning. It is worthless — it
signs tokens for a local database of fake data — and production reads
`Jwt__SigningKey` from the environment and **refuses to start** if it is missing
or shorter than 32 characters.

## Known limits

Stated rather than hidden:

- **No email invitations.** Adding a team member requires them to have signed up
  already. The flow needs a token and an email template; it is not built.
- **Rate limiting is in-process.** Several API replicas would each allow the full
  quota. Redis would fix it.
- **Migrations run on startup**, which suits one instance. With several, two
  would race; that belongs in a release step.
- **Reminder timing is per-business, not per-service** — one lead time for
  everything.
- **No recurring appointments, waitlists, or customer-facing booking history.**

## Licence

MIT.
