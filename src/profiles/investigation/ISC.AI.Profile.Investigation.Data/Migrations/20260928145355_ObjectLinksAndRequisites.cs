using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ISC.AI.Profile.Investigation.Data.Migrations
{
    /// <inheritdoc />
    public partial class ObjectLinksAndRequisites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "link_type_id",
                schema: "investigation",
                table: "person",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "linked_to_person_id",
                schema: "investigation",
                table: "person",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "name_normalized",
                schema: "investigation",
                table: "person",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "residence_normalized",
                schema: "investigation",
                table: "person",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "person_address",
                schema: "investigation",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    person_id = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    text_normalized = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    classification = table.Column<short>(type: "smallint", nullable: false),
                    division_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_person_address", x => x.id);
                    table.ForeignKey(
                        name: "fk_person_address_persons_person_id",
                        column: x => x.person_id,
                        principalSchema: "investigation",
                        principalTable: "person",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "person_vehicle",
                schema: "investigation",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    person_id = table.Column<int>(type: "integer", nullable: false),
                    plate_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    plate_normalized = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    make = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    color = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    classification = table.Column<short>(type: "smallint", nullable: false),
                    division_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_person_vehicle", x => x.id);
                    table.CheckConstraint("ck_person_vehicle_plate_or_make", "coalesce(btrim(plate_number), '') <> '' OR coalesce(btrim(make), '') <> ''");
                    table.ForeignKey(
                        name: "fk_person_vehicle_person_person_id",
                        column: x => x.person_id,
                        principalSchema: "investigation",
                        principalTable: "person",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_person_link_type_id",
                schema: "investigation",
                table: "person",
                column: "link_type_id");

            migrationBuilder.CreateIndex(
                name: "ix_person_linked_to_person_id",
                schema: "investigation",
                table: "person",
                column: "linked_to_person_id");

            migrationBuilder.CreateIndex(
                name: "ix_person_name_normalized",
                schema: "investigation",
                table: "person",
                column: "name_normalized");

            migrationBuilder.CreateIndex(
                name: "ix_person_residence_normalized",
                schema: "investigation",
                table: "person",
                column: "residence_normalized");

            migrationBuilder.AddCheckConstraint(
                name: "ck_person_link_only_for_link_role",
                schema: "investigation",
                table: "person",
                sql: "(role = 2 OR (linked_to_person_id IS NULL AND link_type_id IS NULL)) AND (linked_to_person_id IS NULL OR linked_to_person_id <> id)");

            migrationBuilder.CreateIndex(
                name: "ix_person_address_person_id",
                schema: "investigation",
                table: "person_address",
                column: "person_id");

            migrationBuilder.CreateIndex(
                name: "ix_person_address_text_normalized",
                schema: "investigation",
                table: "person_address",
                column: "text_normalized");

            migrationBuilder.CreateIndex(
                name: "ix_person_vehicle_person_id",
                schema: "investigation",
                table: "person_vehicle",
                column: "person_id");

            migrationBuilder.CreateIndex(
                name: "ix_person_vehicle_plate_normalized",
                schema: "investigation",
                table: "person_vehicle",
                column: "plate_normalized");

            migrationBuilder.AddForeignKey(
                name: "fk_person_person_linked_to_person_id",
                schema: "investigation",
                table: "person",
                column: "linked_to_person_id",
                principalSchema: "investigation",
                principalTable: "person",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_person_reference_items_link_type_id",
                schema: "investigation",
                table: "person",
                column: "link_type_id",
                principalSchema: "investigation",
                principalTable: "reference_item",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_person_person_linked_to_person_id",
                schema: "investigation",
                table: "person");

            migrationBuilder.DropForeignKey(
                name: "fk_person_reference_items_link_type_id",
                schema: "investigation",
                table: "person");

            migrationBuilder.DropTable(
                name: "person_address",
                schema: "investigation");

            migrationBuilder.DropTable(
                name: "person_vehicle",
                schema: "investigation");

            migrationBuilder.DropIndex(
                name: "ix_person_link_type_id",
                schema: "investigation",
                table: "person");

            migrationBuilder.DropIndex(
                name: "ix_person_linked_to_person_id",
                schema: "investigation",
                table: "person");

            migrationBuilder.DropIndex(
                name: "ix_person_name_normalized",
                schema: "investigation",
                table: "person");

            migrationBuilder.DropIndex(
                name: "ix_person_residence_normalized",
                schema: "investigation",
                table: "person");

            migrationBuilder.DropCheckConstraint(
                name: "ck_person_link_only_for_link_role",
                schema: "investigation",
                table: "person");

            migrationBuilder.DropColumn(
                name: "link_type_id",
                schema: "investigation",
                table: "person");

            migrationBuilder.DropColumn(
                name: "linked_to_person_id",
                schema: "investigation",
                table: "person");

            migrationBuilder.DropColumn(
                name: "name_normalized",
                schema: "investigation",
                table: "person");

            migrationBuilder.DropColumn(
                name: "residence_normalized",
                schema: "investigation",
                table: "person");
        }
    }
}
