using Bookline.Api.Data;
using Bookline.Api.Domain;
using Bookline.Api.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bookline.Api.Services.Notifications;

/// <summary>
/// Drains the outbox: picks up due messages, sends them, and records the
/// outcome. Reminders are simply messages with a future SendAfterUtc, so one
/// loop handles confirmations and reminders alike.
///
/// Delivery is at-least-once. A send that succeeds but whose row fails to
/// update will be retried, so a customer may occasionally get a duplicate —
/// the right trade for a reminder, where silence is worse than repetition.
/// </summary>
public class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    IOptions<NotificationOptions> options,
    TimeProvider clock,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private readonly NotificationOptions _options = options.Value;

    private const int BatchSize = 25;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.DispatchIntervalSeconds));

        logger.LogInformation("Outbox dispatcher started, polling every {Interval}.", interval);

        using var timer = new PeriodicTimer(interval, clock);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A background service that throws is a background service
                // that silently stops. Log and keep the loop alive.
                logger.LogError(ex, "Outbox dispatch failed; continuing.");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal async Task<int> DispatchBatchAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();

        // No HTTP request, so no tenant: the dispatcher works across all of
        // them by design, and says so explicitly rather than by accident.
        using var _ = tenant.BypassForBackgroundWork();

        var now = clock.GetUtcNow().UtcDateTime;

        var due = await db.OutboxMessages
            .Where(m => m.SentAt == null
                        && m.SendAfterUtc <= now
                        && m.AttemptCount < _options.MaxAttempts)
            .OrderBy(m => m.SendAfterUtc)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (due.Count == 0)
        {
            return 0;
        }

        var sent = 0;

        foreach (var message in due)
        {
            message.AttemptCount++;

            try
            {
                await sender.SendAsync(message.Recipient, message.Subject, message.Body, ct);

                message.SentAt = clock.GetUtcNow().UtcDateTime;
                message.LastError = null;
                sent++;
            }
            catch (Exception ex)
            {
                message.LastError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;

                // Exponential backoff, so a dead mail server is retried a few
                // times over hours rather than hammered every fifteen seconds.
                var delayMinutes = Math.Pow(5, message.AttemptCount);
                message.SendAfterUtc = clock.GetUtcNow().UtcDateTime.AddMinutes(delayMinutes);

                logger.LogWarning(ex,
                    "Failed to deliver outbox message {MessageId}, attempt {Attempt} of {Max}.",
                    message.Id, message.AttemptCount, _options.MaxAttempts);
            }

            if (message.Kind == NotificationKind.BookingReminder && message.SentAt is not null)
            {
                await MarkReminderSentAsync(db, message, ct);
            }
        }

        await db.SaveChangesAsync(ct);

        return sent;
    }

    private static async Task MarkReminderSentAsync(AppDbContext db, OutboxMessage message, CancellationToken ct)
    {
        if (message.AppointmentId is null)
        {
            return;
        }

        var appointment = await db.Appointments.FirstOrDefaultAsync(a => a.Id == message.AppointmentId, ct);

        if (appointment is not null)
        {
            appointment.ReminderSentAt = message.SentAt;
        }
    }
}
