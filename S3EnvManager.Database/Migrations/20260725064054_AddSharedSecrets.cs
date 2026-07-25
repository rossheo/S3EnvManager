using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace S3EnvManager.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddSharedSecrets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SharedSecrets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    DataKeyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharedSecrets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SharedSecrets_DataKeyGenerations_DataKeyId",
                        column: x => x.DataKeyId,
                        principalTable: "DataKeyGenerations",
                        principalColumn: "KeyId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SharedSecretAppGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SharedSecretId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    GrantedByUserId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharedSecretAppGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SharedSecretAppGrants_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SharedSecretAppGrants_SharedSecrets_SharedSecretId",
                        column: x => x.SharedSecretId,
                        principalTable: "SharedSecrets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SharedSecretReferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SharedSecretId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsOverwriteBundle = table.Column<bool>(type: "boolean", nullable: false),
                    KeyName = table.Column<string>(type: "text", nullable: false),
                    LastMaterializedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharedSecretReferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SharedSecretReferences_Envs_EnvId",
                        column: x => x.EnvId,
                        principalTable: "Envs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SharedSecretReferences_SharedSecrets_SharedSecretId",
                        column: x => x.SharedSecretId,
                        principalTable: "SharedSecrets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SharedSecretAppGrants_AppId",
                table: "SharedSecretAppGrants",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_SharedSecretAppGrants_SharedSecretId_AppId",
                table: "SharedSecretAppGrants",
                columns: new[] { "SharedSecretId", "AppId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SharedSecretReferences_EnvId_IsOverwriteBundle_KeyName",
                table: "SharedSecretReferences",
                columns: new[] { "EnvId", "IsOverwriteBundle", "KeyName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SharedSecretReferences_SharedSecretId",
                table: "SharedSecretReferences",
                column: "SharedSecretId");

            migrationBuilder.CreateIndex(
                name: "IX_SharedSecrets_DataKeyId",
                table: "SharedSecrets",
                column: "DataKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_SharedSecrets_Name",
                table: "SharedSecrets",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SharedSecretAppGrants");

            migrationBuilder.DropTable(
                name: "SharedSecretReferences");

            migrationBuilder.DropTable(
                name: "SharedSecrets");
        }
    }
}
