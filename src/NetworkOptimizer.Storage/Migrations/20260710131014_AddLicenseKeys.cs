using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddLicenseKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LicenseKeyRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LicenseKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Org = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Model = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    SiteAllowance = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    IssuedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PaidThrough = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PerpetualConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    ActivatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastCheckAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextCheckAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastCheckError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    EntitlementJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LicenseKeyRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SiteLicenseAssignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SiteId = table.Column<int>(type: "INTEGER", nullable: false),
                    LicenseKeyRecordId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteLicenseAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SiteLicenseAssignments_LicenseKeyRecords_LicenseKeyRecordId",
                        column: x => x.LicenseKeyRecordId,
                        principalTable: "LicenseKeyRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SiteLicenseAssignments_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LicenseKeyRecords_LicenseKey",
                table: "LicenseKeyRecords",
                column: "LicenseKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LicenseKeyRecords_NextCheckAt",
                table: "LicenseKeyRecords",
                column: "NextCheckAt");

            migrationBuilder.CreateIndex(
                name: "IX_LicenseKeyRecords_Status",
                table: "LicenseKeyRecords",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_SiteLicenseAssignments_LicenseKeyRecordId",
                table: "SiteLicenseAssignments",
                column: "LicenseKeyRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_SiteLicenseAssignments_SiteId",
                table: "SiteLicenseAssignments",
                column: "SiteId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SiteLicenseAssignments");

            migrationBuilder.DropTable(
                name: "LicenseKeyRecords");
        }
    }
}
