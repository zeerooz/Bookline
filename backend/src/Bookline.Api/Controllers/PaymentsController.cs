using Bookline.Api.Data;
using Bookline.Api.Domain;
using Bookline.Api.Security;
using Bookline.Api.Services;
using Bookline.Api.Services.Payments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bookline.Api.Controllers;

/// <summary>
/// Gateway callbacks.
///
/// Deliberately not under /api/t/{tenantSlug}: a provider calls one URL for
/// the whole platform and knows nothing about tenants. The payment is found by
/// its provider reference, and the tenant is derived from that — which is one
/// of the only two places in this codebase allowed to read across tenants.
/// </summary>
[ApiController]
[Route("api/payments")]
public class PaymentsController(
    AppDbContext db,
    ITenantContext tenant,
    IPaymentProvider provider,
    INotificationQueue notifications,
    TimeProvider clock,
    ILogger<PaymentsController> logger) : ControllerBase
{
    [HttpPost("webhook")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Webhook(CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        var payload = await reader.ReadToEndAsync(ct);

        var signature = Request.Headers["Stripe-Signature"].FirstOrDefault();
        var result = await provider.ParseWebhookAsync(payload, signature, ct);

        if (result is null)
        {
            // Either unverifiable or an event type we do not act on. Answer
            // 200 so the provider stops retrying something we will never
            // process, and log it rather than failing loudly.
            logger.LogInformation("Ignored an unrecognised or unverified payment webhook.");
            return Ok();
        }

        // Explicit, scoped lift of tenant filtering. The webhook has no tenant
        // in its URL, and the payment row is what tells us which one it is.
        using var _ = tenant.BypassForBackgroundWork();

        var payment = await db.Payments
            .Include(p => p.Appointment)
            .FirstOrDefaultAsync(p => p.ProviderReference == result.Reference, ct);

        if (payment?.Appointment is null)
        {
            logger.LogWarning("Payment webhook referenced unknown reference {Reference}", result.Reference);
            return Ok();
        }

        // Providers retry, so the same event can arrive several times. Acting
        // only on a pending payment makes this idempotent.
        if (payment.Status != PaymentStatus.Pending)
        {
            return Ok();
        }

        var appointment = payment.Appointment;

        if (result.Succeeded)
        {
            payment.Status = PaymentStatus.Succeeded;
            payment.CompletedAt = clock.GetUtcNow().UtcDateTime;
            appointment.Status = AppointmentStatus.Confirmed;

            var business = await db.Tenants.FirstAsync(t => t.Id == appointment.TenantId, ct);
            var service = await db.Services.FirstAsync(s => s.Id == appointment.ServiceId, ct);
            var staff = await db.StaffMembers.FirstAsync(s => s.Id == appointment.StaffMemberId, ct);

            // The confirmation was held back until the money arrived.
            notifications.QueueBookingConfirmed(appointment, service, staff, business);
            notifications.QueueReminder(appointment, service, staff, business);
        }
        else
        {
            payment.Status = PaymentStatus.Failed;
            payment.FailureReason = result.FailureReason;

            // Release the held slot so somebody else can take it.
            appointment.Status = AppointmentStatus.Cancelled;
            appointment.CancelledAt = clock.GetUtcNow().UtcDateTime;
            appointment.CancellationReason = "Payment was not completed.";
        }

        db.AuditEntries.Add(new AuditEntry
        {
            TenantId = appointment.TenantId,
            Action = result.Succeeded ? "payment.succeeded" : "payment.failed",
            EntityType = nameof(Payment),
            EntityId = payment.Id.ToString(),
            Detail = $"{payment.AmountMinorUnits} {payment.Currency} via {payment.Provider}",
        });

        await db.SaveChangesAsync(ct);

        return Ok();
    }
}
