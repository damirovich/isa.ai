using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Pgvector;

#nullable disable

namespace ISC.AI.Modules.Media.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialMedia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "media");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "asset",
                schema: "media",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    original_file_name = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    stored_file_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    byte_size = table.Column<long>(type: "bigint", nullable: false),
                    duration_ms = table.Column<long>(type: "bigint", nullable: true),
                    source = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    classification = table.Column<short>(type: "smallint", nullable: false),
                    division_id = table.Column<int>(type: "integer", nullable: false),
                    is_current = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    uploaded_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    index_status = table.Column<int>(type: "integer", nullable: false),
                    index_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    detector_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    embedder_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    indexed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asset", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "frame",
                schema: "media",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    asset_id = table.Column<int>(type: "integer", nullable: false),
                    index = table.Column<int>(type: "integer", nullable: false),
                    timestamp_ms = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_frame", x => x.id);
                    table.ForeignKey(
                        name: "fk_frame_asset_asset_id",
                        column: x => x.asset_id,
                        principalSchema: "media",
                        principalTable: "asset",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "face",
                schema: "media",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    asset_id = table.Column<int>(type: "integer", nullable: false),
                    frame_id = table.Column<int>(type: "integer", nullable: true),
                    box_x = table.Column<float>(type: "real", nullable: false),
                    box_y = table.Column<float>(type: "real", nullable: false),
                    box_width = table.Column<float>(type: "real", nullable: false),
                    box_height = table.Column<float>(type: "real", nullable: false),
                    landmarks = table.Column<float[]>(type: "real[]", nullable: false),
                    detection_score = table.Column<float>(type: "real", nullable: false),
                    quality_score = table.Column<float>(type: "real", nullable: false),
                    quality_acceptable = table.Column<bool>(type: "boolean", nullable: false),
                    quality_reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    crop_stored_file_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    track_id = table.Column<int>(type: "integer", nullable: true),
                    classification = table.Column<short>(type: "smallint", nullable: false),
                    division_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_face", x => x.id);
                    table.ForeignKey(
                        name: "fk_face_assets_asset_id",
                        column: x => x.asset_id,
                        principalSchema: "media",
                        principalTable: "asset",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_face_frames_frame_id",
                        column: x => x.frame_id,
                        principalSchema: "media",
                        principalTable: "frame",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "face_template",
                schema: "media",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    face_id = table.Column<int>(type: "integer", nullable: false),
                    asset_id = table.Column<int>(type: "integer", nullable: false),
                    embedding = table.Column<Vector>(type: "vector(128)", nullable: false),
                    model_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    quality_acceptable = table.Column<bool>(type: "boolean", nullable: false),
                    classification = table.Column<short>(type: "smallint", nullable: false),
                    division_id = table.Column<int>(type: "integer", nullable: false),
                    is_current = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_face_template", x => x.id);
                    table.ForeignKey(
                        name: "fk_face_template_assets_asset_id",
                        column: x => x.asset_id,
                        principalSchema: "media",
                        principalTable: "asset",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_face_template_face_face_id",
                        column: x => x.face_id,
                        principalSchema: "media",
                        principalTable: "face",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_asset_classification_division_id",
                schema: "media",
                table: "asset",
                columns: new[] { "classification", "division_id" });

            migrationBuilder.CreateIndex(
                name: "ix_asset_division_id_content_hash",
                schema: "media",
                table: "asset",
                columns: new[] { "division_id", "content_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_asset_index_status",
                schema: "media",
                table: "asset",
                column: "index_status");

            migrationBuilder.CreateIndex(
                name: "ix_asset_stored_file_name",
                schema: "media",
                table: "asset",
                column: "stored_file_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_face_asset_id",
                schema: "media",
                table: "face",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_face_classification_division_id",
                schema: "media",
                table: "face",
                columns: new[] { "classification", "division_id" });

            migrationBuilder.CreateIndex(
                name: "ix_face_crop_stored_file_name",
                schema: "media",
                table: "face",
                column: "crop_stored_file_name",
                unique: true,
                filter: "crop_stored_file_name IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_face_frame_id",
                schema: "media",
                table: "face",
                column: "frame_id");

            migrationBuilder.CreateIndex(
                name: "ix_face_template_asset_id",
                schema: "media",
                table: "face_template",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_face_template_classification_division_id",
                schema: "media",
                table: "face_template",
                columns: new[] { "classification", "division_id" });

            migrationBuilder.CreateIndex(
                name: "ix_face_template_embedding",
                schema: "media",
                table: "face_template",
                column: "embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" })
                .Annotation("Npgsql:StorageParameter:ef_construction", 512)
                .Annotation("Npgsql:StorageParameter:m", 16);

            migrationBuilder.CreateIndex(
                name: "ix_face_template_face_id",
                schema: "media",
                table: "face_template",
                column: "face_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_frame_asset_id_index",
                schema: "media",
                table: "frame",
                columns: new[] { "asset_id", "index" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "face_template",
                schema: "media");

            migrationBuilder.DropTable(
                name: "face",
                schema: "media");

            migrationBuilder.DropTable(
                name: "frame",
                schema: "media");

            migrationBuilder.DropTable(
                name: "asset",
                schema: "media");
        }
    }
}
