using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundLedger.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// MODEL-ONLY migration (Phase 2): maps fund_types, payment_modes, accounts, opening_balances,
    /// categories and transactions (and their enums) into the EF model. The tables, indexes, triggers
    /// and policies already exist from <c>InitialSchema</c>, so there is no DDL here.
    /// </summary>
    public partial class FinanceModel : Migration
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
