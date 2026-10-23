using System.Globalization;
using Bookline.Api.Data;
using Bookline.Api.Domain;
using Bookline.Api.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bookline.Api.Services;

/// <summary>
/// Writes notifications into the outbox table. Nothing here sends anything —
/// messages are committed alongside the appointment that caused them, and a
/// background dispatcher delivers them afterwards. That ordering is what stops
/// a customer being emailed about a booking whose transaction rolled back.
/// </summary>
public interface INotificationQueue
{
    void QueueBookingConfirmed(Appointment appointment, Service service, StaffMember staff, Tenant tenant);
    void QueueBookingCancelled(Appointment appointment, Service service, StaffMember staff, Tenant tenant);
    void QueueReminder(Appointment appointment, Service service, StaffMember staff, Tenant tenant);
    void CancelPendingReminders(Guid appointmentId);
}

public class NotificationQueue(AppDbContext db, IOptions<NotificationOptions> options, TimeProvider clock)
    : INotificationQueue
{
    private readonly NotificationOptions _options = options.Value;

    public void QueueBookingConfirmed(Appointment appointment, Service service, StaffMember staff, Tenant tenant)
    {
        var when = FormatLocal(appointment.StartsAtUtc, tenant.TimeZoneId);

        var body = string.Join('\n',
            $"Hello {appointment.CustomerName},",
            string.Empty,
            "Your appointment is confirmed.",
            string.Empty,
            $"What:  {service.Name}",
            $"Who:   {staff.DisplayName}",
            $"When:  {when}",
            $"Where: {tenant.Name}",
            string.Empty,
            "Need to change it? Use this link:",
            $"/booking/{appointment.ManagementToken}");

        Queue(appointment, tenant, NotificationKind.BookingConfirmed, clock.GetUtcNow().UtcDateTime,
            $"Your booking at {tenant.Name} is confirmed", body);
    }

    public void QueueBookingCancelled(Appointment appointment, Service service, StaffMember staff, Tenant tenant)
    {
        var when = FormatLocal(appointment.StartsAtUtc, tenant.TimeZoneId);

        var body = string.Join('\n',
            $"Hello {appointment.CustomerName},",
            string.Empty,
            $"Your {service.Name} appointment with {staff.DisplayName} on {when} has been cancelled.",
            string.Empty,
            "You can book another time whenever suits you.");

        Queue(appointment, tenant, NotificationKind.BookingCancelled, clock.GetUtcNow().UtcDateTime,
            $"Your booking at {tenant.Name} was cancelled", body);
    }

    public void QueueReminder(Appointment appointment, Service service, StaffMember staff, Tenant tenant)
    {
        var sendAt = appointment.StartsAtUtc.AddMinutes(-_options.ReminderLeadMinutes);

        // An appointment booked inside the reminder window gets no reminder:
        // the confirmation it just received is the reminder.
        if (sendAt <= clock.GetUtcNow().UtcDateTime)
        {
            return;
        }

        var when = FormatLocal(appointment.StartsAtUtc, tenant.TimeZoneId);

        var body = string.Join('\n',
            $"Hello {appointment.CustomerName},",
            string.Empty,
            "A reminder of your appointment.",
            string.Empty,
            $"What: {service.Name}",
            $"Who:  {staff.DisplayName}",
            $"When: {when}",
            string.Empty,
            "Cannot make it? Cancel here:",
            $"/booking/{appointment.ManagementToken}");

        Queue(appointment, tenant, NotificationKind.BookingReminder, sendAt,
            $"Reminder: {service.Name} at {tenant.Name}", body);
    }

    public void CancelPendingReminders(Guid appointmentId)
    {
        // Messages queued earlier in this same unit of work exist only in the
        // change tracker, so they have to be dropped separately from the ones
        // already committed to the table.
        foreach (var message in db.OutboxMessages.Local
                     .Where(m => m.AppointmentId == appointmentId && m.SentAt is null)
                     .ToList())
        {
            db.Entry(message).State = EntityState.Detached;
        }

        db.OutboxMessages
            .Where(m => m.AppointmentId == appointmentId
                        && m.SentAt == null
                        && m.Kind == NotificationKind.BookingReminder)
            .ExecuteDelete();
    }

    private void Queue(
        Appointment appointment, Tenant tenant, NotificationKind kind, DateTime sendAfter, string subject, string body)
    {
        db.OutboxMessages.Add(new OutboxMessage
        {
            TenantId = tenant.Id,
            Kind = kind,
            Channel = NotificationChannel.Email,
            Recipient = appointment.CustomerEmail,
            Subject = subject,
            Body = body,
            SendAfterUtc = sendAfter,
            AppointmentId = appointment.Id,
        });
    }

    private static string FormatLocal(DateTime utc, string timeZoneId)
    {
        var zone = BookingService.ResolveTimeZone(timeZoneId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);

        return local.ToString("dddd d MMMM yyyy 'at' HH:mm", CultureInfo.InvariantCulture);
    }
}
