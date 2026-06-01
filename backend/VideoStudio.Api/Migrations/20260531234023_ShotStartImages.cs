using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VideoStudio.Api.Migrations
{
    /// <inheritdoc />
    public partial class ShotStartImages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "StartImageAssetId",
                table: "Shots",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StartImagePath",
                table: "Shots",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StartImageUrl",
                table: "Shots",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ShotId",
                table: "Assets",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Shots_StartImageAssetId",
                table: "Shots",
                column: "StartImageAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_Assets_ShotId",
                table: "Assets",
                column: "ShotId");

            migrationBuilder.AddForeignKey(
                name: "FK_Assets_Shots_ShotId",
                table: "Assets",
                column: "ShotId",
                principalTable: "Shots",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Shots_Assets_StartImageAssetId",
                table: "Shots",
                column: "StartImageAssetId",
                principalTable: "Assets",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Assets_Shots_ShotId",
                table: "Assets");

            migrationBuilder.DropForeignKey(
                name: "FK_Shots_Assets_StartImageAssetId",
                table: "Shots");

            migrationBuilder.DropIndex(
                name: "IX_Shots_StartImageAssetId",
                table: "Shots");

            migrationBuilder.DropIndex(
                name: "IX_Assets_ShotId",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "StartImageAssetId",
                table: "Shots");

            migrationBuilder.DropColumn(
                name: "StartImagePath",
                table: "Shots");

            migrationBuilder.DropColumn(
                name: "StartImageUrl",
                table: "Shots");

            migrationBuilder.DropColumn(
                name: "ShotId",
                table: "Assets");
        }
    }
}
