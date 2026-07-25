using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace S3EnvManager.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddKeyExpiration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "KeyExpirations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsOverwriteBundle = table.Column<bool>(type: "boolean", nullable: false),
                    KeyName = table.Column<string>(type: "text", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KeyExpirations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KeyExpirations_Envs_EnvId",
                        column: x => x.EnvId,
                        principalTable: "Envs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KeyExpirations_EnvId_IsOverwriteBundle_KeyName",
                table: "KeyExpirations",
                columns: new[] { "EnvId", "IsOverwriteBundle", "KeyName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KeyExpirations");
        }
    }
}
