using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISC.AI.Profile.Inspector.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRemediationDeadline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "remediation_deadline",
                schema: "inspector",
                table: "violation",
                type: "date",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_violation_remediation_status_remediation_deadline",
                schema: "inspector",
                table: "violation",
                columns: new[] { "remediation_status", "remediation_deadline" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_violation_remediation_status_remediation_deadline",
                schema: "inspector",
                table: "violation");

            migrationBuilder.DropColumn(
                name: "remediation_deadline",
                schema: "inspector",
                table: "violation");
        }
    }
}
