using System.Net;
using System.Net.Http.Json;
using System.Text;
using Bookline.Api.Data;
using Bookline.Api.Domain;
using Bookline.Api.Dtos;
using Bookline.Api.Security;
using Bookline.Api.Services.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bookline.Tests.Integration;

public class PaymentAndReminderTests(BooklineFactory factory) : IClassFixture<BooklineFactory>
{
    [Fact]
    public async Task A_prepaid_service_holds_the_slot_and_returns_a_payment_link()
    {
        var business = await factory.CreateBusinessAsync(requiresPrepayment: true);
        var guest = factory.CreateClient();

        var response = await guest.BookAsync(
            business.Slug, Scenario.BookingAt(business.ServiceId, Scenario.SlotAt(10)));

        response.EnsureSuccessStatusCode();
        var confirmation = await response.Content.ReadFromJsonAsync<BookingConfirmation>();

        Assert.Equal(AppointmentStatus.PendingPayment, confirmation!.Appointment.Status);
        Assert.False(string.IsNullOrWhiteSpace(confirmation.PaymentUrl));
    }

    [Fact]
    public async Task A_slot_awaiting_payment_cannot_be_taken_by_someone_else()
    {
        var business = await factory.CreateBusinessAsync(requiresPrepayment: true);
        var guest = factory.CreateClient();
        var slot = Scenario.SlotAt(11);

        (await guest.BookAsync(business.Slug, Scenario.BookingAt(business.ServiceId, slot)))
            .EnsureSuccessStatusCode();

        // Holding the slot during checkout is the point: nobody should lose a
        // booking because they were typing their card number.
        var second = await guest.BookAsync(business.Slug, Scenario.BookingAt(business.ServiceId, slot));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task No_confirmation_is_sent_before_payment_succeeds()
    {
        var business = await factory.CreateBusinessAsync(requiresPrepayment: true);
        var guest = factory.CreateClient();

        var created = await guest.BookAsync(
            business.Slug, Scenario.BookingAt(business.ServiceId, Scenario.SlotAt(12)));
        var confirmation = await created.Content.ReadFromJsonAsync<BookingConfirmation>();

        var queued = await QueuedForAsync(confirmation!.Appointment.Id);

        Assert.Empty(queued);
    }

    [Fact]
    public async Task A_successful_payment_confirms_the_booking_and_queues_the_email()
    {
        var business = await factory.CreateBusinessAsync(requiresPrepayment: true);
        var guest = factory.CreateClient();

        var created = await guest.BookAsync(
            business.Slug, Scenario.BookingAt(business.ServiceId, Scenario.SlotAt(13)));
        var confirmation = await created.Content.ReadFromJsonAsync<BookingConfirmation>();

        var reference = await ReferenceForAsync(confirmation!.Appointment.Id);
        (await SendWebhookAsync(guest, reference, succeeded: true)).EnsureSuccessStatusCode();

        var appointment = await ReloadAsync(confirmation.Appointment.Id);
        Assert.Equal(AppointmentStatus.Confirmed, appointment.Status);

        var queued = await QueuedForAsync(confirmation.Appointment.Id);
        Assert.Contains(queued, m => m.Kind == NotificationKind.BookingConfirmed);
    }

    [Fact]
    public async Task A_failed_payment_releases_the_slot()
    {
        var business = await factory.CreateBusinessAsync(requiresPrepayment: true);
        var guest = factory.CreateClient();
        var slot = Scenario.SlotAt(14);

        var created = await guest.BookAsync(business.Slug, Scenario.BookingAt(business.ServiceId, slot));
        var confirmation = await created.Content.ReadFromJsonAsync<BookingConfirmation>();

        var reference = await ReferenceForAsync(confirmation!.Appointment.Id);
        (await SendWebhookAsync(guest, reference, succeeded: false)).EnsureSuccessStatusCode();

        var appointment = await ReloadAsync(confirmation.Appointment.Id);
        Assert.Equal(AppointmentStatus.Cancelled, appointment.Status);

        // Somebody else can now take the time.
        var rebooked = await guest.BookAsync(business.Slug, Scenario.BookingAt(business.ServiceId, slot));
        Assert.Equal(HttpStatusCode.Created, rebooked.StatusCode);
    }

    [Fact]
    public async Task Replaying_the_same_webhook_changes_nothing()
    {
        var business = await factory.CreateBusinessAsync(requiresPrepayment: true);
        var guest = factory.CreateClient();

        var created = await guest.BookAsync(
            business.Slug, Scenario.BookingAt(business.ServiceId, Scenario.SlotAt(15)));
        var confirmation = await created.Content.ReadFromJsonAsync<BookingConfirmation>();

        var reference = await ReferenceForAsync(confirmation!.Appointment.Id);

        await SendWebhookAsync(guest, reference, succeeded: true);
        await SendWebhookAsync(guest, reference, succeeded: true);
        await SendWebhookAsync(guest, reference, succeeded: true);

        var queued = await QueuedForAsync(confirmation.Appointment.Id);

        // Providers retry. Three deliveries must not mean three emails.
        Assert.Single(queued, m => m.Kind == NotificationKind.BookingConfirmed);
    }

    [Fact]
    public async Task A_webhook_for_an_unknown_reference_is_ignored_quietly()
    {
        var guest = factory.CreateClient();

        var response = await SendWebhookAsync(guest, "fake_does_not_exist", succeeded: true);

        // 200 on purpose: a 4xx or 5xx makes the provider retry forever.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_malformed_webhook_body_does_not_crash_the_endpoint()
    {
        var guest = factory.CreateClient();

        var response = await guest.PostAsync("/api/payments/webhook",
            new StringContent("not json at all", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Cancelling_a_booking_drops_its_pending_reminder()
    {
        var business = await factory.CreateBusinessAsync();
        var guest = factory.CreateClient();

        var created = await guest.BookAsync(
            business.Slug, Scenario.BookingAt(business.ServiceId, Scenario.SlotAt(16)));
        var confirmation = await created.Content.ReadFromJsonAsync<BookingConfirmation>();

        Assert.Contains(await QueuedForAsync(confirmation!.Appointment.Id),
            m => m.Kind == NotificationKind.BookingReminder);

        await guest.PostAsJsonAsync(
            $"/api/public/{business.Slug}/bookings/{confirmation.ManagementToken}/cancel",
            new CancelBookingRequest());

        var remaining = await QueuedForAsync(confirmation.Appointment.Id);

        // Reminding someone about an appointment they cancelled is worse than
        // sending nothing at all.
        Assert.DoesNotContain(remaining, m => m.Kind == NotificationKind.BookingReminder && m.SentAt is null);
        Assert.Contains(remaining, m => m.Kind == NotificationKind.BookingCancelled);
    }

    [Fact]
    public async Task The_dispatcher_sends_due_messages_and_leaves_future_ones_alone()
    {
        var business = await factory.CreateBusinessAsync();
        var guest = factory.CreateClient();

        var created = await guest.BookAsync(
            business.Slug, Scenario.BookingAt(business.ServiceId, Scenario.SlotAt(10, 0, addDays: 30)));
        var confirmation = await created.Content.ReadFromJsonAsync<BookingConfirmation>();

        var dispatcher = ActivatorUtilities.CreateInstance<OutboxDispatcher>(factory.Services);
        var sent = await dispatcher.DispatchBatchAsync(CancellationToken.None);

        Assert.True(sent > 0);

        var messages = await QueuedForAsync(confirmation!.Appointment.Id);

        // The confirmation was due immediately; the reminder is scheduled for
        // the day before the appointment and must still be waiting.
        Assert.All(messages.Where(m => m.Kind == NotificationKind.BookingConfirmed),
            m => Assert.NotNull(m.SentAt));
        Assert.All(messages.Where(m => m.Kind == NotificationKind.BookingReminder),
            m => Assert.Null(m.SentAt));
    }

    [Fact]
    public async Task The_dispatcher_works_across_every_tenant()
    {
        var first = await factory.CreateBusinessAsync();
        var second = await factory.CreateBusinessAsync();
        var guest = factory.CreateClient();

        await guest.BookAsync(first.Slug, Scenario.BookingAt(first.ServiceId, Scenario.SlotAt(9, 0, addDays: 40)));
        await guest.BookAsync(second.Slug, Scenario.BookingAt(second.ServiceId, Scenario.SlotAt(9, 0, addDays: 40)));

        var dispatcher = ActivatorUtilities.CreateInstance<OutboxDispatcher>(factory.Services);
        await dispatcher.DispatchBatchAsync(CancellationToken.None);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var unsentConfirmations = await db.OutboxMessages
            .IgnoreQueryFilters()
            .CountAsync(m => m.Kind == NotificationKind.BookingConfirmed && m.SentAt == null);

        // A dispatcher that silently only drained one tenant would be a very
        // quiet bug, so it is asserted directly.
        Assert.Equal(0, unsentConfirmations);
    }

    // -----------------------------------------------------------------------

    private static Task<HttpResponseMessage> SendWebhookAsync(HttpClient client, string reference, bool succeeded) =>
        client.PostAsync("/api/payments/webhook", new StringContent(
            $$"""{"reference":"{{reference}}","succeeded":{{(succeeded ? "true" : "false")}}}""",
            Encoding.UTF8,
            "application/json"));

    private async Task<string> ReferenceForAsync(Guid appointmentId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var payment = await db.Payments
            .IgnoreQueryFilters()
            .FirstAsync(p => p.AppointmentId == appointmentId);

        return payment.ProviderReference!;
    }

    private async Task<Appointment> ReloadAsync(Guid appointmentId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.Appointments.IgnoreQueryFilters().AsNoTracking().FirstAsync(a => a.Id == appointmentId);
    }

    private async Task<List<OutboxMessage>> QueuedForAsync(Guid appointmentId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.OutboxMessages
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(m => m.AppointmentId == appointmentId)
            .ToListAsync();
    }
}
