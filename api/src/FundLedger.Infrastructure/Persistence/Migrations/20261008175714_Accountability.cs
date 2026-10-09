using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundLedger.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Phase 3. The only DDL is replacing <c>fl.guard_transactions()</c> so that cancelling a
    /// transaction also bumps its revision (each edit and the cancellation get their own history
    /// entry). The EF model additionally gains the <c>transaction_revisions</c> entity and a
    /// concurrency token on <c>transactions.revision</c>; the table and constraints already exist
    /// from <c>InitialSchema</c>, so nothing is created here. <c>database/schema.sql</c> carries the
    /// same function (CI proves the two match).
    /// </summary>
    public partial class Accountability : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(SqlResource.Read("CancelBumpsRevision.up.sql"));

        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(SqlResource.Read("CancelBumpsRevision.down.sql"));
    }
}
