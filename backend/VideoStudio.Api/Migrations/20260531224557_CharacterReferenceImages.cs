using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VideoStudio.Api.Migrations
{
    /// <inheritdoc />
    public partial class CharacterReferenceImages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ReferenceAssetId",
                table: "Characters",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReferenceImageUrl",
                table: "Characters",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CharacterId",
                table: "Assets",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MediaUrl",
                table: "Assets",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalFileName",
                table: "Assets",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "StoragePath",
                table: "Assets",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Type",
                table: "Assets",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "InputImage");

            migrationBuilder.CreateIndex(
                name: "IX_Characters_ReferenceAssetId",
                table: "Characters",
                column: "ReferenceAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_Assets_CharacterId",
                table: "Assets",
                column: "CharacterId");

            migrationBuilder.AddForeignKey(
                name: "FK_Assets_Characters_CharacterId",
                table: "Assets",
                column: "CharacterId",
                principalTable: "Characters",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Characters_Assets_ReferenceAssetId",
                table: "Characters",
                column: "ReferenceAssetId",
                principalTable: "Assets",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Assets_Characters_CharacterId",
                table: "Assets");

            migrationBuilder.DropForeignKey(
                name: "FK_Characters_Assets_ReferenceAssetId",
                table: "Characters");

            migrationBuilder.DropIndex(
                name: "IX_Characters_ReferenceAssetId",
                table: "Characters");

            migrationBuilder.DropIndex(
                name: "IX_Assets_CharacterId",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "ReferenceAssetId",
                table: "Characters");

            migrationBuilder.DropColumn(
                name: "ReferenceImageUrl",
                table: "Characters");

            migrationBuilder.DropColumn(
                name: "CharacterId",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "MediaUrl",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "OriginalFileName",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "StoragePath",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "Assets");
        }
    }
}
