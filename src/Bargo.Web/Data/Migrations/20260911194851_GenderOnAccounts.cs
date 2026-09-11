using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bargo.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class GenderOnAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Gender",
                table: "Shippers",
                type: "nvarchar(6)",
                maxLength: 6,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Gender",
                table: "Drivers",
                type: "nvarchar(6)",
                maxLength: 6,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Gender",
                table: "Shippers");

            migrationBuilder.DropColumn(
                name: "Gender",
                table: "Drivers");
        }
    }
}
