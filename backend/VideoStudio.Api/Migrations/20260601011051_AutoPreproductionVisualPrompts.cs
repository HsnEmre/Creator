using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VideoStudio.Api.Migrations
{
    /// <inheritdoc />
    public partial class AutoPreproductionVisualPrompts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StartImageNegativePrompt",
                table: "Shots",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StartImagePrompt",
                table: "Shots",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StartImageStatus",
                table: "Shots",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "CharacterId",
                table: "RenderJobs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReferenceImageNegativePrompt",
                table: "Characters",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReferenceImagePrompt",
                table: "Characters",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReferenceStatus",
                table: "Characters",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_RenderJobs_CharacterId",
                table: "RenderJobs",
                column: "CharacterId");

            migrationBuilder.AddForeignKey(
                name: "FK_RenderJobs_Characters_CharacterId",
                table: "RenderJobs",
                column: "CharacterId",
                principalTable: "Characters",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RenderJobs_Characters_CharacterId",
                table: "RenderJobs");

            migrationBuilder.DropIndex(
                name: "IX_RenderJobs_CharacterId",
                table: "RenderJobs");

            migrationBuilder.DropColumn(
                name: "StartImageNegativePrompt",
                table: "Shots");

            migrationBuilder.DropColumn(
                name: "StartImagePrompt",
                table: "Shots");

            migrationBuilder.DropColumn(
                name: "StartImageStatus",
                table: "Shots");

            migrationBuilder.DropColumn(
                name: "CharacterId",
                table: "RenderJobs");

            migrationBuilder.DropColumn(
                name: "ReferenceImageNegativePrompt",
                table: "Characters");

            migrationBuilder.DropColumn(
                name: "ReferenceImagePrompt",
                table: "Characters");

            migrationBuilder.DropColumn(
                name: "ReferenceStatus",
                table: "Characters");
        }
    }
}
