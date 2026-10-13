using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundLedger.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Phase 3b. <c>fl.transaction_attachments</c> already exists (InitialSchema); this migration only brings the EF model
    /// up to date with it (the new entity and its index names), so nothing is created or changed in the database.
    /// </summary>
    public partial class Attachments : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
