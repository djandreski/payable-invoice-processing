using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace InvoiceReviewAssistant.Infrastructure.Persistence;

/// <summary>Used only by EF tooling; runtime composition is owned by the application host.</summary>
public sealed class InvoiceDbContextFactory : IDesignTimeDbContextFactory<InvoiceDbContext>
{
    public InvoiceDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<InvoiceDbContext>()
            .UseSqlite("Data Source=invoice-review-assistant-design-time.db")
            .Options;
        return new InvoiceDbContext(options);
    }
}
