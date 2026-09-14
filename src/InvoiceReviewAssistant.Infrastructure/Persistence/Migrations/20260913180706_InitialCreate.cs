using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvoiceReviewAssistant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Invoices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    DraftVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    LastValidatedVersion = table.Column<int>(type: "INTEGER", nullable: true),
                    CurrentValidationRunId = table.Column<Guid>(type: "TEXT", nullable: true),
                    NormalizedSupplierName = table.Column<string>(type: "TEXT", nullable: true),
                    NormalizedInvoiceNumber = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DocumentTextSource = table.Column<string>(type: "TEXT", nullable: true),
                    SupplierName = table.Column<string>(type: "TEXT", nullable: true),
                    SupplierRegistrationId = table.Column<string>(type: "TEXT", nullable: true),
                    InvoiceNumber = table.Column<string>(type: "TEXT", nullable: true),
                    PurchaseOrderNumber = table.Column<string>(type: "TEXT", nullable: true),
                    InvoiceDate = table.Column<string>(type: "TEXT", nullable: true),
                    DueDate = table.Column<string>(type: "TEXT", nullable: true),
                    PaymentTerms = table.Column<string>(type: "TEXT", nullable: true),
                    NormalizedPaymentTermsDays = table.Column<int>(type: "INTEGER", nullable: true),
                    Currency = table.Column<string>(type: "TEXT", nullable: true),
                    Subtotal = table.Column<string>(type: "TEXT", nullable: true),
                    TaxAmount = table.Column<string>(type: "TEXT", nullable: true),
                    Total = table.Column<string>(type: "TEXT", nullable: true),
                    ReviewNotes = table.Column<string>(type: "TEXT", nullable: true),
                    ProcessingFailureStage = table.Column<string>(type: "TEXT", nullable: true),
                    ProcessingFailureCode = table.Column<string>(type: "TEXT", nullable: true),
                    ProcessingFailureMessage = table.Column<string>(type: "TEXT", nullable: true),
                    ProcessingFailedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DecisionKind = table.Column<string>(type: "TEXT", nullable: true),
                    DecidedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RejectionReason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invoices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InvoiceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", nullable: false),
                    Actor = table.Column<string>(type: "TEXT", nullable: false),
                    DraftVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DataJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuditEvents_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InvoiceDocuments",
                columns: table => new
                {
                    InvoiceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StorageKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    OriginalFilename = table.Column<string>(type: "TEXT", nullable: false),
                    ByteLength = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", nullable: false),
                    PageCount = table.Column<int>(type: "INTEGER", nullable: false),
                    IntegrityStatus = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceDocuments", x => x.InvoiceId);
                    table.ForeignKey(
                        name: "FK_InvoiceDocuments_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InvoiceFieldMetadata",
                columns: table => new
                {
                    InvoiceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FieldKey = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalValueJson = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalSource = table.Column<string>(type: "TEXT", nullable: false),
                    CurrentSource = table.Column<string>(type: "TEXT", nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: true),
                    LastCorrectedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceFieldMetadata", x => new { x.InvoiceId, x.FieldKey });
                    table.ForeignKey(
                        name: "FK_InvoiceFieldMetadata_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ValidationRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DraftVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    ValidatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ValidationRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ValidationRuns_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FieldCorrections",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InvoiceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AuditEventId = table.Column<long>(type: "INTEGER", nullable: false),
                    DraftVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    FieldKey = table.Column<string>(type: "TEXT", nullable: false),
                    PreviousValueJson = table.Column<string>(type: "TEXT", nullable: false),
                    NewValueJson = table.Column<string>(type: "TEXT", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FieldCorrections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FieldCorrections_AuditEvents_AuditEventId",
                        column: x => x.AuditEventId,
                        principalTable: "AuditEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FieldCorrections_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ValidationResults",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ValidationRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RuleCode = table.Column<string>(type: "TEXT", nullable: false),
                    Severity = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    RelatedFieldsJson = table.Column<string>(type: "TEXT", nullable: false),
                    DataJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ValidationResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ValidationResults_ValidationRuns_ValidationRunId",
                        column: x => x.ValidationRunId,
                        principalTable: "ValidationRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_InvoiceId_OccurredAtUtc_Id",
                table: "AuditEvents",
                columns: new[] { "InvoiceId", "OccurredAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_FieldCorrections_AuditEventId",
                table: "FieldCorrections",
                column: "AuditEventId");

            migrationBuilder.CreateIndex(
                name: "IX_FieldCorrections_InvoiceId",
                table: "FieldCorrections",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceDocuments_StorageKey",
                table: "InvoiceDocuments",
                column: "StorageKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_NormalizedSupplierName_NormalizedInvoiceNumber",
                table: "Invoices",
                columns: new[] { "NormalizedSupplierName", "NormalizedInvoiceNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_Status_UpdatedAtUtc",
                table: "Invoices",
                columns: new[] { "Status", "UpdatedAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_UpdatedAtUtc",
                table: "Invoices",
                column: "UpdatedAtUtc",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_ValidationResults_ValidationRunId",
                table: "ValidationResults",
                column: "ValidationRunId");

            migrationBuilder.CreateIndex(
                name: "IX_ValidationRuns_InvoiceId_ValidatedAtUtc",
                table: "ValidationRuns",
                columns: new[] { "InvoiceId", "ValidatedAtUtc" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FieldCorrections");

            migrationBuilder.DropTable(
                name: "InvoiceDocuments");

            migrationBuilder.DropTable(
                name: "InvoiceFieldMetadata");

            migrationBuilder.DropTable(
                name: "ValidationResults");

            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropTable(
                name: "ValidationRuns");

            migrationBuilder.DropTable(
                name: "Invoices");
        }
    }
}
