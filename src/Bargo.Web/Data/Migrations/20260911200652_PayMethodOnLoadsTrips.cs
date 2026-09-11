using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bargo.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class PayMethodOnLoadsTrips : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PayMethod",
                table: "Trips",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PayMethod",
                table: "Loads",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PayMethod",
                table: "Trips");

            migrationBuilder.DropColumn(
                name: "PayMethod",
                table: "Loads");
        }
    }
}
