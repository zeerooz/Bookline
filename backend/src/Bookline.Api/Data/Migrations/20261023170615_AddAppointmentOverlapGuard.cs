using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bookline.Api.Data.Migrations;

/// <summary>
/// Makes double booking impossible at the database level.
///
/// The application already checks availability before writing, but a check
/// followed by an insert is not atomic: two customers clicking the same slot a
/// millisecond apart can both see it free. Serialising every booking would
/// solve it at the cost of throughput. A Postgres exclusion constraint solves
/// it exactly, in the one place that can actually arbitrate — the database
/// rejects the second writer and the API turns that into "pick another slot".
///
/// btree_gist is required because the constraint mixes an equality test on a
/// uuid with an overlap test on a range, and stock GiST cannot do the former.
/// </summary>
public partial class AddAppointmentOverlapGuard : Migration
{
    private const string ConstraintName = "ck_appointments_no_staff_overlap";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gist;");

        // Statuses 0, 1 and 3 are PendingPayment, Confirmed and Completed —
        // the ones that occupy a staff member's time. Cancelled and NoShow
        // free the slot, so they are excluded from the guard.
        migrationBuilder.Sql($"""
            ALTER TABLE "Appointments"
            ADD CONSTRAINT "{ConstraintName}"
            EXCLUDE USING gist (
                "StaffMemberId" WITH =,
                tstzrange("StartsAtUtc", "BlockedUntilUtc", '[)') WITH &&
            )
            WHERE ("Status" IN (0, 1, 3));
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($"""ALTER TABLE "Appointments" DROP CONSTRAINT IF EXISTS "{ConstraintName}";""");

        // btree_gist is left installed: another table may have come to rely on
        // it, and dropping a shared extension during a rollback is worse than
        // leaving it in place.
    }
}
