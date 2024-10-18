using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderVerificationAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddBranchTable1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "BranchName",
                table: "Branches",
                newName: "Name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Name",
                table: "Branches",
                newName: "BranchName");
        }
    }
}
