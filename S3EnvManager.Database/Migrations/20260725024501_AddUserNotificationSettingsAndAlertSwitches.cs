using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace S3EnvManager.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddUserNotificationSettingsAndAlertSwitches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserNotificationAlertSwitches",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    AlertType = table.Column<string>(type: "text", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserNotificationAlertSwitches", x => new { x.UserId, x.AlertType });
                    table.ForeignKey(
                        name: "FK_UserNotificationAlertSwitches_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserNotificationSettings",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    ProtectedDiscordWebhookUrl = table.Column<string>(type: "text", nullable: true),
                    NotifyDaysBeforeExpiration = table.Column<int>(type: "integer", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserNotificationSettings", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_UserNotificationSettings_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserNotificationAlertSwitches");

            migrationBuilder.DropTable(
                name: "UserNotificationSettings");
        }
    }
}
