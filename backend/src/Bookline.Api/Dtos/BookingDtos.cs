using System.ComponentModel.DataAnnotations;
using Bookline.Api.Domain;

namespace Bookline.Api.Dtos;

public record TenantSummary(
    Guid Id,
    string Name,
    string Slug,
    string TimeZoneId,
    string Currency,
    int BookingHorizonDays);

public record ServiceResponse(
    Guid Id,
    string Name,
    string? Description,
    int DurationMinutes,
    int BufferMinutes,
    int PriceMinorUnits,
    bool RequiresPrepayment,
    bool IsActive);

public record ServiceRequest
{
    [Required, MaxLength(200)] public string Name { get; init; } = string.Empty;
    [MaxLength(2000)] public string? Description { get; init; }
    [Range(5, 1440)] public int DurationMinutes { get; init; } = 30;
    [Range(0, 480)] public int BufferMinutes { get; init; }
    [Range(0, 100_000_00)] public int PriceMinorUnits { get; init; }
    public bool RequiresPrepayment { get; init; }
    public bool IsActive { get; init; } = true;

    /// <summary>Staff who can deliver this service. Empty means nobody can book it yet.</summary>
    public List<Guid> StaffMemberIds { get; init; } = [];
}

public record StaffMemberResponse(
    Guid Id,
    string DisplayName,
    string? Title,
    bool IsActive,
    IReadOnlyList<Guid> ServiceIds);

public record StaffMemberRequest
{
    [Required, MaxLength(200)] public string DisplayName { get; init; } = string.Empty;
    [MaxLength(200)] public string? Title { get; init; }
    public bool IsActive { get; init; } = true;
    public List<Guid> ServiceIds { get; init; } = [];
}

public record BusinessHourRequest
{
    [Range(0, 6)] public int DayOfWeek { get; init; }

    /// <summary>Minutes from local midnight. 540 is 09:00.</summary>
    [Range(0, 1440)] public int StartMinute { get; init; }
    [Range(0, 1440)] public int EndMinute { get; init; }

    /// <summary>Null applies the window to the whole business.</summary>
    public Guid? StaffMemberId { get; init; }
}

public record BusinessHourResponse(Guid Id, int DayOfWeek, int StartMinute, int EndMinute, Guid? StaffMemberId);

public record AvailabilityResponse(
    Guid ServiceId,
    Guid StaffMemberId,
    string StaffDisplayName,
    string TimeZoneId,
    IReadOnlyList<DateTime> Slots);

public record CreateBookingRequest
{
    [Required] public Guid ServiceId { get; init; }

    /// <summary>Null lets the platform pick any staff member who is free.</summary>
    public Guid? StaffMemberId { get; init; }

    [Required] public DateTime StartsAtUtc { get; init; }

    [Required, MaxLength(200)] public string CustomerName { get; init; } = string.Empty;
    [Required, EmailAddress, MaxLength(256)] public string CustomerEmail { get; init; } = string.Empty;
    [MaxLength(32)] public string? CustomerPhone { get; init; }
    [MaxLength(2000)] public string? Notes { get; init; }
}

public record AppointmentResponse(
    Guid Id,
    Guid ServiceId,
    string ServiceName,
    Guid StaffMemberId,
    string StaffDisplayName,
    string CustomerName,
    string CustomerEmail,
    string? CustomerPhone,
    DateTime StartsAtUtc,
    DateTime EndsAtUtc,
    AppointmentStatus Status,
    int PriceMinorUnits,
    string Currency,
    string? Notes,
    DateTime CreatedAt);

/// <summary>Returned once, to whoever created the booking. Carries the management token.</summary>
public record BookingConfirmation(
    AppointmentResponse Appointment,
    string ManagementToken,
    string? PaymentUrl);

public record CancelBookingRequest
{
    [MaxLength(500)] public string? Reason { get; init; }
}

public record AuditEntryResponse(
    long Id,
    string Action,
    string EntityType,
    string? EntityId,
    string? Detail,
    string? ActorEmail,
    DateTime CreatedAt);
