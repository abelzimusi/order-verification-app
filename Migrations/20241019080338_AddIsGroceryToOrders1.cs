using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderVerificationAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddIsGroceryToOrders1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsGrocery",
                table: "Orders",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsGrocery",
                table: "Orders");
        }
    }
}
