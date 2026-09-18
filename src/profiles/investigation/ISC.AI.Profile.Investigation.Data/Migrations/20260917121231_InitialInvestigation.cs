using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ISC.AI.Profile.Investigation.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialInvestigation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "investigation");

            migrationBuilder.CreateTable(
                name: "case_file",
                schema: "investigation",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    opened_at = table.Column<DateOnly>(type: "date", nullable: false),
                    investigator_user_id = table.Column<int>(type: "integer", nullable: true),
                    division_id = table.Column<int>(type: "integer", nullable: false),
                    classification = table.Column<short>(type: "smallint", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    basis = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    closed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_case_file", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "division",
                schema: "investigation",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    parent_id = table.Column<int>(type: "integer", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_division", x => x.id);
                    table.ForeignKey(
                        name: "fk_division_division_parent_id",
                        column: x => x.parent_id,
                        principalSchema: "investigation",
                        principalTable: "division",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_role_assignment",
                schema: "investigation",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    role = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_role_assignment", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "case_document_link",
                schema: "investigation",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    case_id = table.Column<int>(type: "integer", nullable: false),
                    doc_flow_document_id = table.Column<int>(type: "integer", nullable: false),
                    linked_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_case_document_link", x => x.id);
                    table.ForeignKey(
                        name: "fk_case_document_link_cases_case_id",
                        column: x => x.case_id,
                        principalSchema: "investigation",
                        principalTable: "case_file",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "case_media_link",
                schema: "investigation",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    case_id = table.Column<int>(type: "integer", nullable: false),
                    media_asset_id = table.Column<int>(type: "integer", nullable: false),
                    place = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    linked_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_case_media_link", x => x.id);
                    table.ForeignKey(
                        name: "fk_case_media_link_case_file_case_id",
                        column: x => x.case_id,
                        principalSchema: "investigation",
                        principalTable: "case_file",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "person",
                schema: "investigation",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    case_id = table.Column<int>(type: "integer", nullable: false),
                    display_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    is_unidentified = table.Column<bool>(type: "boolean", nullable: false),
                    unidentified_number = table.Column<int>(type: "integer", nullable: true),
                    role_in_case = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    classification = table.Column<short>(type: "smallint", nullable: false),
                    division_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_person", x => x.id);
                    table.ForeignKey(
                        name: "fk_person_case_file_case_id",
                        column: x => x.case_id,
                        principalSchema: "investigation",
                        principalTable: "case_file",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "search_authorization",
                schema: "investigation",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    case_id = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    reference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    issued_at = table.Column<DateOnly>(type: "date", nullable: false),
                    issued_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    valid_until = table.Column<DateOnly>(type: "date", nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_search_authorization", x => x.id);
                    table.ForeignKey(
                        name: "fk_search_authorization_case_file_case_id",
                        column: x => x.case_id,
                        principalSchema: "investigation",
                        principalTable: "case_file",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "appearance",
                schema: "investigation",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    person_id = table.Column<int>(type: "integer", nullable: false),
                    case_id = table.Column<int>(type: "integer", nullable: false),
                    media_asset_id = table.Column<int>(type: "integer", nullable: false),
                    media_face_id = table.Column<int>(type: "integer", nullable: false),
                    frame_index = table.Column<int>(type: "integer", nullable: true),
                    frame_timestamp_ms = table.Column<long>(type: "bigint", nullable: true),
                    search_session_id = table.Column<int>(type: "integer", nullable: false),
                    candidate_id = table.Column<int>(type: "integer", nullable: false),
                    similarity = table.Column<double>(type: "double precision", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    confirmed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expert_user_id = table.Column<int>(type: "integer", nullable: false),
                    verifier_user_id = table.Column<int>(type: "integer", nullable: false),
                    classification = table.Column<short>(type: "smallint", nullable: false),
                    division_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_appearance", x => x.id);
                    table.ForeignKey(
                        name: "fk_appearance_persons_person_id",
                        column: x => x.person_id,
                        principalSchema: "investigation",
                        principalTable: "person",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "reference_photo",
                schema: "investigation",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    person_id = table.Column<int>(type: "integer", nullable: false),
                    media_asset_id = table.Column<int>(type: "integer", nullable: false),
                    media_face_id = table.Column<int>(type: "integer", nullable: true),
                    quality_score = table.Column<float>(type: "real", nullable: true),
                    source = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    legal_basis = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    review_due_at = table.Column<DateOnly>(type: "date", nullable: true),
                    added_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    superseded_by_id = table.Column<int>(type: "integer", nullable: true),
                    classification = table.Column<short>(type: "smallint", nullable: false),
                    division_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reference_photo", x => x.id);
                    table.ForeignKey(
                        name: "fk_reference_photo_person_person_id",
                        column: x => x.person_id,
                        principalSchema: "investigation",
                        principalTable: "person",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_appearance_candidate_id",
                schema: "investigation",
                table: "appearance",
                column: "candidate_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_appearance_case_id",
                schema: "investigation",
                table: "appearance",
                column: "case_id");

            migrationBuilder.CreateIndex(
                name: "ix_appearance_media_asset_id",
                schema: "investigation",
                table: "appearance",
                column: "media_asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_appearance_person_id",
                schema: "investigation",
                table: "appearance",
                column: "person_id");

            migrationBuilder.CreateIndex(
                name: "ix_case_document_link_case_id_doc_flow_document_id",
                schema: "investigation",
                table: "case_document_link",
                columns: new[] { "case_id", "doc_flow_document_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_case_file_classification_division_id",
                schema: "investigation",
                table: "case_file",
                columns: new[] { "classification", "division_id" });

            migrationBuilder.CreateIndex(
                name: "ix_case_file_division_id_number",
                schema: "investigation",
                table: "case_file",
                columns: new[] { "division_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_case_file_investigator_user_id",
                schema: "investigation",
                table: "case_file",
                column: "investigator_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_case_file_status",
                schema: "investigation",
                table: "case_file",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_case_media_link_case_id_media_asset_id",
                schema: "investigation",
                table: "case_media_link",
                columns: new[] { "case_id", "media_asset_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_case_media_link_media_asset_id",
                schema: "investigation",
                table: "case_media_link",
                column: "media_asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_division_code",
                schema: "investigation",
                table: "division",
                column: "code",
                unique: true,
                filter: "code IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_division_parent_id",
                schema: "investigation",
                table: "division",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_person_case_id_unidentified_number",
                schema: "investigation",
                table: "person",
                columns: new[] { "case_id", "unidentified_number" },
                unique: true,
                filter: "unidentified_number IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_reference_photo_media_asset_id",
                schema: "investigation",
                table: "reference_photo",
                column: "media_asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_reference_photo_person_id",
                schema: "investigation",
                table: "reference_photo",
                column: "person_id");

            migrationBuilder.CreateIndex(
                name: "ix_search_authorization_case_id",
                schema: "investigation",
                table: "search_authorization",
                column: "case_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_role_assignment_user_id",
                schema: "investigation",
                table: "user_role_assignment",
                column: "user_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "appearance",
                schema: "investigation");

            migrationBuilder.DropTable(
                name: "case_document_link",
                schema: "investigation");

            migrationBuilder.DropTable(
                name: "case_media_link",
                schema: "investigation");

            migrationBuilder.DropTable(
                name: "division",
                schema: "investigation");

            migrationBuilder.DropTable(
                name: "reference_photo",
                schema: "investigation");

            migrationBuilder.DropTable(
                name: "search_authorization",
                schema: "investigation");

            migrationBuilder.DropTable(
                name: "user_role_assignment",
                schema: "investigation");

            migrationBuilder.DropTable(
                name: "person",
                schema: "investigation");

            migrationBuilder.DropTable(
                name: "case_file",
                schema: "investigation");
        }
    }
}
