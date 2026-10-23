namespace Bookline.Api.Domain;

/// <summary>
/// A queued notification (confirmation, reminder, cancellation).
///
/// Written in the same transaction as the appointment it describes, so a
/// booking can never be saved without its email also being queued, and an
/// email can never be queued for a booking that rolled back. A background
/// worker drains the table and retries failures with backoff.
/// </summary>
public class OutboxMessage : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    public NotificationKind Kind { get; set; }
    public NotificationChannel Channel { get; set; }

    public string Recipient { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;

    /// <summary>Earliest time this should be sent. Reminders are scheduled ahead.</summary>
    public DateTime SendAfterUtc { get; set; } = DateTime.UtcNow;

    public DateTime? SentAt { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }

    public Guid? AppointmentId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum NotificationKind
{
    BookingConfirmed = 0,
    BookingCancelled = 1,
    BookingReminder = 2,
    StaffNewBooking = 3
}

public enum NotificationChannel
{
    Email = 0,
    Sms = 1
}
