using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bargo.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class SmsMarketingCampaigns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MarketingContacts",
                columns: table => new
                {
                    MarketingContactId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Mobile = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Category = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Province = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    City = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Lat = table.Column<double>(type: "float", nullable: true),
                    Lng = table.Column<double>(type: "float", nullable: true),
                    Source = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    OptedOut = table.Column<bool>(type: "bit", nullable: false),
                    OptedOutAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SentCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketingContacts", x => x.MarketingContactId);
                });

            migrationBuilder.CreateTable(
                name: "SmsCampaigns",
                columns: table => new
                {
                    SmsCampaignId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Title = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    FilterCategory = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    FilterProvince = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    FilterCity = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Cap = table.Column<int>(type: "int", nullable: false),
                    Total = table.Column<int>(type: "int", nullable: false),
                    SentCount = table.Column<int>(type: "int", nullable: false),
                    FailedCount = table.Column<int>(type: "int", nullable: false),
                    CreatedByAdminId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SmsCampaigns", x => x.SmsCampaignId);
                });

            migrationBuilder.CreateTable(
                name: "SmsCampaignRecipients",
                columns: table => new
                {
                    SmsCampaignRecipientId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CampaignId = table.Column<int>(type: "int", nullable: false),
                    ContactId = table.Column<int>(type: "int", nullable: false),
                    Mobile = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    Error = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SentAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SmsCampaignRecipients", x => x.SmsCampaignRecipientId);
                    table.ForeignKey(
                        name: "FK_SmsCampaignRecipients_MarketingContacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "MarketingContacts",
                        principalColumn: "MarketingContactId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SmsCampaignRecipients_SmsCampaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "SmsCampaigns",
                        principalColumn: "SmsCampaignId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MarketingContacts_Category",
                table: "MarketingContacts",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_MarketingContacts_Mobile",
                table: "MarketingContacts",
                column: "Mobile",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MarketingContacts_Province_City",
                table: "MarketingContacts",
                columns: new[] { "Province", "City" });

            migrationBuilder.CreateIndex(
                name: "IX_SmsCampaignRecipients_CampaignId_Status",
                table: "SmsCampaignRecipients",
                columns: new[] { "CampaignId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_SmsCampaignRecipients_ContactId",
                table: "SmsCampaignRecipients",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_SmsCampaignRecipients_Status_SentAt",
                table: "SmsCampaignRecipients",
                columns: new[] { "Status", "SentAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SmsCampaigns_Status",
                table: "SmsCampaigns",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SmsCampaignRecipients");

            migrationBuilder.DropTable(
                name: "MarketingContacts");

            migrationBuilder.DropTable(
                name: "SmsCampaigns");
        }
    }
}
