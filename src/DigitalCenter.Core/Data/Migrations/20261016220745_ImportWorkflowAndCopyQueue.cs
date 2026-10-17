using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DigitalCenter.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ImportWorkflowAndCopyQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MetadataImportLogs_SourceFile",
                table: "MetadataImportLogs");

            migrationBuilder.AddColumn<string>(
                name: "CategoryName",
                table: "MetadataImportLogs",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "FileSize",
                table: "MetadataImportLogs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSeries",
                table: "MetadataImportLogs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MediaTitleId",
                table: "MetadataImportLogs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Note",
                table: "MetadataImportLogs",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ParsedEpisode",
                table: "MetadataImportLogs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ParsedSeason",
                table: "MetadataImportLogs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParsedTitle",
                table: "MetadataImportLogs",
                type: "TEXT",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ParsedYear",
                table: "MetadataImportLogs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourcePath",
                table: "MetadataImportLogs",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Stage",
                table: "MetadataImportLogs",
                type: "TEXT",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetPath",
                table: "MetadataImportLogs",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CopyJobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MediaFileId = table.Column<int>(type: "INTEGER", nullable: true),
                    SourcePath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    RelativeTargetPath = table.Column<string>(type: "TEXT", maxLength: 600, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    TitleDisplay = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    ConflictNote = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    DestinationRoot = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    BytesCopied = table.Column<long>(type: "INTEGER", nullable: true),
                    Verified = table.Column<bool>(type: "INTEGER", nullable: false),
                    QueuedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CopyJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CopyJobs_MediaFiles_MediaFileId",
                        column: x => x.MediaFileId,
                        principalTable: "MediaFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MetadataImportLogs_MediaTitleId",
                table: "MetadataImportLogs",
                column: "MediaTitleId");

            migrationBuilder.CreateIndex(
                name: "IX_MetadataImportLogs_SourcePath",
                table: "MetadataImportLogs",
                column: "SourcePath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CopyJobs_MediaFileId",
                table: "CopyJobs",
                column: "MediaFileId");

            migrationBuilder.CreateIndex(
                name: "IX_CopyJobs_QueuedAt",
                table: "CopyJobs",
                column: "QueuedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CopyJobs_Status",
                table: "CopyJobs",
                column: "Status");

            migrationBuilder.AddForeignKey(
                name: "FK_MetadataImportLogs_MediaTitles_MediaTitleId",
                table: "MetadataImportLogs",
                column: "MediaTitleId",
                principalTable: "MediaTitles",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MetadataImportLogs_MediaTitles_MediaTitleId",
                table: "MetadataImportLogs");

            migrationBuilder.DropTable(
                name: "CopyJobs");

            migrationBuilder.DropIndex(
                name: "IX_MetadataImportLogs_MediaTitleId",
                table: "MetadataImportLogs");

            migrationBuilder.DropIndex(
                name: "IX_MetadataImportLogs_SourcePath",
                table: "MetadataImportLogs");

            migrationBuilder.DropColumn(
                name: "CategoryName",
                table: "MetadataImportLogs");

            migrationBuilder.DropColumn(
                name: "FileSize",
                table: "MetadataImportLogs");

            migrationBuilder.DropColumn(
                name: "IsSeries",
                table: "MetadataImportLogs");

            migrationBuilder.DropColumn(
                name: "MediaTitleId",
                table: "MetadataImportLogs");

            migrationBuilder.DropColumn(
                name: "Note",
                table: "MetadataImportLogs");

            migrationBuilder.DropColumn(
                name: "ParsedEpisode",
                table: "MetadataImportLogs");

            migrationBuilder.DropColumn(
                name: "ParsedSeason",
                table: "MetadataImportLogs");

            migrationBuilder.DropColumn(
                name: "ParsedTitle",
                table: "MetadataImportLogs");

            migrationBuilder.DropColumn(
                name: "ParsedYear",
                table: "MetadataImportLogs");

            migrationBuilder.DropColumn(
                name: "SourcePath",
                table: "MetadataImportLogs");

            migrationBuilder.DropColumn(
                name: "Stage",
                table: "MetadataImportLogs");

            migrationBuilder.DropColumn(
                name: "TargetPath",
                table: "MetadataImportLogs");

            migrationBuilder.CreateIndex(
                name: "IX_MetadataImportLogs_SourceFile",
                table: "MetadataImportLogs",
                column: "SourceFile");
        }
    }
}
