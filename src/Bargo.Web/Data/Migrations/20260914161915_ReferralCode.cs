using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bargo.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReferralCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReferralCode",
                table: "Shippers",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReferralCode",
                table: "Drivers",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReferralCode",
                table: "Shippers");

            migrationBuilder.DropColumn(
                name: "ReferralCode",
                table: "Drivers");
        }
    }
}
