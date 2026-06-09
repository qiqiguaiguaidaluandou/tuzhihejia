using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TZHJ.Gateway.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_records",
                columns: table => new
                {
                    audit_id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    flow = table.Column<int>(type: "integer", nullable: false),
                    employee_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    batch_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    window_start = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    window_end = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    target = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    row_count = table.Column<int>(type: "integer", nullable: false),
                    submitted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_records", x => x.audit_id);
                });

            migrationBuilder.CreateIndex(
                name: "idx_audit_lookup",
                table: "audit_records",
                columns: new[] { "flow", "employee_id", "window_start", "window_end" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_records");
        }
    }
}
