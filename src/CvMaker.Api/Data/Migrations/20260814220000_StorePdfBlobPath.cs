using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CvMaker.Api.Data.Migrations
{
    /// <summary>
    /// Catches the migration set up with the model.
    ///
    /// When generated PDFs moved out of the database and into Blob Storage,
    /// <c>GenerationJobEntity.Pdf</c> (the bytes) became <c>PdfBlobPath</c> (a key). The model
    /// changed and no migration followed it, so the two drifted: <c>InitialCreate</c> still
    /// creates a <c>Pdf</c> column and no <c>PdfBlobPath</c>.
    ///
    /// EF Core 9 refuses to migrate a model with pending changes at all, so this was not a
    /// latent problem — <c>CvMaker.Api</c> could not start against a fresh database, which is
    /// every first deploy and every CI run of the end-to-end job. It went unnoticed because
    /// that job died earlier, in the render image build.
    ///
    /// The column is dropped rather than converted. A blob path cannot be derived from PDF
    /// bytes, so there is nothing to migrate; any existing row loses a PDF it can regenerate.
    /// </summary>
    public partial class StorePdfBlobPath : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Pdf",
                table: "GenerationJobs");

            migrationBuilder.AddColumn<string>(
                name: "PdfBlobPath",
                table: "GenerationJobs",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PdfBlobPath",
                table: "GenerationJobs");

            migrationBuilder.AddColumn<byte[]>(
                name: "Pdf",
                table: "GenerationJobs",
                type: "bytea",
                nullable: true);
        }
    }
}
