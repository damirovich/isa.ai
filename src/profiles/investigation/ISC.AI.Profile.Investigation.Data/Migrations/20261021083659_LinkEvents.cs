using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ISC.AI.Profile.Investigation.Data.Migrations
{
    /// <inheritdoc />
    public partial class LinkEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "link_event",
                schema: "investigation",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    occurred_on = table.Column<DateOnly>(type: "date", nullable: true),
                    description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    created_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    classification = table.Column<short>(type: "smallint", nullable: false),
                    division_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_link_event", x => x.id);
                    table.CheckConstraint("ck_link_event_kind", "kind BETWEEN 1 AND 4");
                    table.CheckConstraint("ck_link_event_title", "btrim(title) <> ''");
                });

            migrationBuilder.CreateTable(
                name: "link_event_participant",
                schema: "investigation",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    event_id = table.Column<int>(type: "integer", nullable: false),
                    person_id = table.Column<int>(type: "integer", nullable: false),
                    role = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    level = table.Column<int>(type: "integer", nullable: false),
                    in_search = table.Column<bool>(type: "boolean", nullable: false),
                    vehicle_plate = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    vehicle_plate_normalized = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    vehicle_details = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    contact_address = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    contact_address_normalized = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    classification = table.Column<short>(type: "smallint", nullable: false),
                    division_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_link_event_participant", x => x.id);
                    table.CheckConstraint("ck_link_event_participant_level", "level BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "fk_link_event_participant_link_event_event_id",
                        column: x => x.event_id,
                        principalSchema: "investigation",
                        principalTable: "link_event",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_link_event_participant_persons_person_id",
                        column: x => x.person_id,
                        principalSchema: "investigation",
                        principalTable: "person",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_link_event_participant_contact_address_normalized",
                schema: "investigation",
                table: "link_event_participant",
                column: "contact_address_normalized");

            migrationBuilder.CreateIndex(
                name: "ix_link_event_participant_event_id_person_id",
                schema: "investigation",
                table: "link_event_participant",
                columns: new[] { "event_id", "person_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_link_event_participant_person_id",
                schema: "investigation",
                table: "link_event_participant",
                column: "person_id");

            migrationBuilder.CreateIndex(
                name: "ix_link_event_participant_vehicle_plate_normalized",
                schema: "investigation",
                table: "link_event_participant",
                column: "vehicle_plate_normalized");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "link_event_participant",
                schema: "investigation");

            migrationBuilder.DropTable(
                name: "link_event",
                schema: "investigation");
        }
    }
}
