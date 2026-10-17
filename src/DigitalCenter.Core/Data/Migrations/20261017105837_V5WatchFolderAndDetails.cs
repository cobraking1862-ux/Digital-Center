using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DigitalCenter.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class V5WatchFolderAndDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Character",
                table: "TitlePeople",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProfilePath",
                table: "People",
                type: "TEXT",
                maxLength: 600,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TmdbPersonId",
                table: "People",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ChosenFolder",
                table: "MetadataImportLogs",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Confidence",
                table: "MetadataImportLogs",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BackdropPath",
                table: "MediaTitles",
                type: "TEXT",
                maxLength: 600,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImdbId",
                table: "MediaTitles",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetadataOverrides",
                table: "MediaTitles",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalLanguage",
                table: "MediaTitles",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalTitle",
                table: "MediaTitles",
                type: "TEXT",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReleaseDate",
                table: "MediaTitles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TmdbBackdropPath",
                table: "MediaTitles",
                type: "TEXT",
                maxLength: 600,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TmdbPosterPath",
                table: "MediaTitles",
                type: "TEXT",
                maxLength: 600,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TrailerName",
                table: "MediaTitles",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TrailerUrl",
                table: "MediaTitles",
                type: "TEXT",
                maxLength: 600,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "VoteAverage",
                table: "MediaTitles",
                type: "REAL",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_People_TmdbPersonId",
                table: "People",
                column: "TmdbPersonId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaTitles_ImdbId",
                table: "MediaTitles",
                column: "ImdbId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaTitles_OriginalLanguage",
                table: "MediaTitles",
                column: "OriginalLanguage");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_People_TmdbPersonId",
                table: "People");

            migrationBuilder.DropIndex(
                name: "IX_MediaTitles_ImdbId",
                table: "MediaTitles");

            migrationBuilder.DropIndex(
                name: "IX_MediaTitles_OriginalLanguage",
                table: "MediaTitles");

            migrationBuilder.DropColumn(
                name: "Character",
                table: "TitlePeople");

            migrationBuilder.DropColumn(
                name: "ProfilePath",
                table: "People");

            migrationBuilder.DropColumn(
                name: "TmdbPersonId",
                table: "People");

            migrationBuilder.DropColumn(
                name: "ChosenFolder",
                table: "MetadataImportLogs");

            migrationBuilder.DropColumn(
                name: "Confidence",
                table: "MetadataImportLogs");

            migrationBuilder.DropColumn(
                name: "BackdropPath",
                table: "MediaTitles");

            migrationBuilder.DropColumn(
                name: "ImdbId",
                table: "MediaTitles");

            migrationBuilder.DropColumn(
                name: "MetadataOverrides",
                table: "MediaTitles");

            migrationBuilder.DropColumn(
                name: "OriginalLanguage",
                table: "MediaTitles");

            migrationBuilder.DropColumn(
                name: "OriginalTitle",
                table: "MediaTitles");

            migrationBuilder.DropColumn(
                name: "ReleaseDate",
                table: "MediaTitles");

            migrationBuilder.DropColumn(
                name: "TmdbBackdropPath",
                table: "MediaTitles");

            migrationBuilder.DropColumn(
                name: "TmdbPosterPath",
                table: "MediaTitles");

            migrationBuilder.DropColumn(
                name: "TrailerName",
                table: "MediaTitles");

            migrationBuilder.DropColumn(
                name: "TrailerUrl",
                table: "MediaTitles");

            migrationBuilder.DropColumn(
                name: "VoteAverage",
                table: "MediaTitles");
        }
    }
}
