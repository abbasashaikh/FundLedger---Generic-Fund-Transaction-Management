using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundLedger.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Phase 4. <c>fl.export_jobs</c> already exists (InitialSchema); it gains two columns so the finished file
    /// can be kept on the job row until it expires. No EF entity maps the table (it is read and written with SQL),
    /// so the model is unchanged. <c>database/schema.sql</c> carries the same columns.
    /// </summary>
    public partial class Reports : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(SqlResource.Read("ExportContent.up.sql"));

        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(SqlResource.Read("ExportContent.down.sql"));
    }
}
