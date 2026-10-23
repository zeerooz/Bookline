using Bookline.Api.Domain;
using Bookline.Api.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Bookline.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenant)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Service> Services => Set<Service>();
    public DbSet<StaffMember> StaffMembers => Set<StaffMember>();
    public DbSet<StaffService> StaffServices => Set<StaffService>();
    public DbSet<BusinessHour> BusinessHours => Set<BusinessHour>();
    public DbSet<TimeOff> TimeOff => Set<TimeOff>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Tenant>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.TimeZoneId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        });

        b.Entity<ApplicationUser>(e => e.Property(x => x.FullName).HasMaxLength(200));

        b.Entity<Membership>(e =>
        {
            e.Property(x => x.Role).HasConversion<int>();
            // One role per person per business.
            e.HasIndex(x => new { x.TenantId, x.UserId }).IsUnique();
            e.HasOne(x => x.Tenant).WithMany(t => t.Memberships).HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany(u => u.Memberships).HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Service>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(2000);
            e.HasIndex(x => new { x.TenantId, x.IsActive });
            e.HasOne(x => x.Tenant).WithMany(t => t.Services).HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<StaffMember>(e =>
        {
            e.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Title).HasMaxLength(200);
            e.HasIndex(x => new { x.TenantId, x.IsActive });
            e.HasOne(x => x.Tenant).WithMany(t => t.Staff).HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<StaffService>(e =>
        {
            e.HasIndex(x => new { x.StaffMemberId, x.ServiceId }).IsUnique();
            e.HasOne(x => x.StaffMember).WithMany(s => s.StaffServices).HasForeignKey(x => x.StaffMemberId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Service).WithMany(s => s.StaffServices).HasForeignKey(x => x.ServiceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<BusinessHour>(e =>
        {
            e.Property(x => x.DayOfWeek).HasConversion<int>();
            e.HasIndex(x => new { x.TenantId, x.DayOfWeek });
            e.HasOne(x => x.Tenant).WithMany(t => t.BusinessHours).HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.StaffMember).WithMany().HasForeignKey(x => x.StaffMemberId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TimeOff>(e =>
        {
            e.Property(x => x.Reason).HasMaxLength(500);
            e.HasIndex(x => new { x.StaffMemberId, x.StartsAtUtc });
            e.HasOne(x => x.StaffMember).WithMany(s => s.TimeOff).HasForeignKey(x => x.StaffMemberId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Appointment>(e =>
        {
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.CustomerName).HasMaxLength(200).IsRequired();
            e.Property(x => x.CustomerEmail).HasMaxLength(256).IsRequired();
            e.Property(x => x.CustomerPhone).HasMaxLength(32);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.CancellationReason).HasMaxLength(500);
            e.Property(x => x.ManagementToken).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.ManagementToken).IsUnique();

            // The index the availability query actually uses.
            e.HasIndex(x => new { x.TenantId, x.StaffMemberId, x.StartsAtUtc });
            e.HasIndex(x => new { x.TenantId, x.StartsAtUtc });

            // Maps to the Postgres xmin system column, giving optimistic
            // concurrency without an extra column to maintain. Two customers
            // racing for one slot means the loser gets a concurrency failure
            // rather than a double booking.
            e.Property(x => x.Version).IsRowVersion();

            e.HasOne(x => x.Service).WithMany().HasForeignKey(x => x.ServiceId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.StaffMember).WithMany().HasForeignKey(x => x.StaffMemberId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.CustomerUser).WithMany().HasForeignKey(x => x.CustomerUserId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Payment).WithOne(p => p.Appointment!).HasForeignKey<Payment>(p => p.AppointmentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Payment>(e =>
        {
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.Provider).HasMaxLength(32).IsRequired();
            e.Property(x => x.ProviderReference).HasMaxLength(256);
            e.Property(x => x.FailureReason).HasMaxLength(500);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.HasIndex(x => x.ProviderReference);
        });

        b.Entity<AuditEntry>(e =>
        {
            e.Property(x => x.Action).HasMaxLength(64).IsRequired();
            e.Property(x => x.EntityType).HasMaxLength(64).IsRequired();
            e.Property(x => x.EntityId).HasMaxLength(64);
            e.Property(x => x.Detail).HasMaxLength(1000);
            e.Property(x => x.ActorEmail).HasMaxLength(256);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.HasIndex(x => new { x.TenantId, x.CreatedAt });
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.Property(x => x.Kind).HasConversion<int>();
            e.Property(x => x.Channel).HasConversion<int>();
            e.Property(x => x.Recipient).HasMaxLength(256).IsRequired();
            e.Property(x => x.Subject).HasMaxLength(300).IsRequired();
            e.Property(x => x.Body).HasMaxLength(8000).IsRequired();
            e.Property(x => x.LastError).HasMaxLength(1000);
            // The query the dispatcher runs every few seconds.
            e.HasIndex(x => new { x.SentAt, x.SendAfterUtc });
        });

        ApplyTenantFilters(b);
    }

    /// <summary>
    /// Adds a global query filter to every entity implementing
    /// <see cref="ITenantOwned"/>. Doing it by reflection over the model rather
    /// than entity by entity means a newly added entity is covered the moment
    /// it is mapped — the case a human reviewer is most likely to miss.
    /// </summary>
    private void ApplyTenantFilters(ModelBuilder b)
    {
        foreach (var entityType in b.Model.GetEntityTypes())
        {
            if (!typeof(ITenantOwned).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            var parameter = System.Linq.Expressions.Expression.Parameter(entityType.ClrType, "e");

            var tenantId = System.Linq.Expressions.Expression.Property(
                System.Linq.Expressions.Expression.Constant(this),
                nameof(CurrentTenantId));

            var bypassed = System.Linq.Expressions.Expression.Property(
                System.Linq.Expressions.Expression.Constant(this),
                nameof(TenantFilterDisabled));

            var matches = System.Linq.Expressions.Expression.Equal(
                System.Linq.Expressions.Expression.Property(parameter, nameof(ITenantOwned.TenantId)),
                tenantId);

            // bypassed || e.TenantId == currentTenantId
            var body = System.Linq.Expressions.Expression.OrElse(bypassed, matches);

            b.Entity(entityType.ClrType)
                .HasQueryFilter(System.Linq.Expressions.Expression.Lambda(body, parameter));
        }
    }

    /// <summary>Read by the compiled query filter on every query. Public for that reason only.</summary>
    public Guid CurrentTenantId => tenant.FilterValue;

    public bool TenantFilterDisabled => tenant.IsBypassed;

    public override int SaveChanges() => SaveChangesAsync().GetAwaiter().GetResult();

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampTenantOnNewEntities();
        return base.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Fills in TenantId on new rows so no handler has to remember. A caller
    /// that sets it explicitly (the background worker, seeding) keeps its value.
    /// </summary>
    private void StampTenantOnNewEntities()
    {
        if (!tenant.HasTenant)
        {
            return;
        }

        foreach (EntityEntry<ITenantOwned> entry in ChangeTracker.Entries<ITenantOwned>())
        {
            if (entry.State == EntityState.Added && entry.Entity.TenantId == Guid.Empty)
            {
                entry.Entity.TenantId = tenant.FilterValue;
            }
        }
    }
}
