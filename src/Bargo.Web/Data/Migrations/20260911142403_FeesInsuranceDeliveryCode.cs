using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bargo.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class FeesInsuranceDeliveryCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LoadingFee",
                table: "Trips",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "UnloadingFee",
                table: "Trips",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "Vat",
                table: "Trips",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<decimal>(
                name: "VatPercent",
                table: "Trips",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<long>(
                name: "WaybillFee",
                table: "Trips",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryCodeHash",
                table: "Loads",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "InsuranceAmount",
                table: "Loads",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InsuranceType",
                table: "Loads",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LoadingFee",
                table: "Trips");

            migrationBuilder.DropColumn(
                name: "UnloadingFee",
                table: "Trips");

            migrationBuilder.DropColumn(
                name: "Vat",
                table: "Trips");

            migrationBuilder.DropColumn(
                name: "VatPercent",
                table: "Trips");

            migrationBuilder.DropColumn(
                name: "WaybillFee",
                table: "Trips");

            migrationBuilder.DropColumn(
                name: "DeliveryCodeHash",
                table: "Loads");

            migrationBuilder.DropColumn(
                name: "InsuranceAmount",
                table: "Loads");

            migrationBuilder.DropColumn(
                name: "InsuranceType",
                table: "Loads");
        }
    }
}
