using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundLedger.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// MODEL-ONLY migration (Phase 1): maps the existing users, user_sessions, funds and
    /// user_fund_access tables and their PostgreSQL enums into the EF model. The tables,
    /// indexes and enum types already exist from <c>InitialSchema</c> (the verified SQL
    /// baseline), so there is no DDL here — only the model snapshot moves forward.
    /// CI's schema-drift job proves database/schema.sql still equals the migrated database.
    /// </summary>
    public partial class IdentityModel : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty: see summary.
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty: nothing was created.
        }
    }
}
