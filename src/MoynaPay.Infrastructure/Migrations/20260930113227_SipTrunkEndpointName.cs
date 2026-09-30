using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoynaPay.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SipTrunkEndpointName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "endpoint_name",
                schema: "telephony",
                table: "sip_trunks",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "endpoint_name",
                schema: "telephony",
                table: "sip_trunks");
        }
    }
}
