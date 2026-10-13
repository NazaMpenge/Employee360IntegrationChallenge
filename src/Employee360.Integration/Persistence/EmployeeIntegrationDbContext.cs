using Microsoft.EntityFrameworkCore;

namespace Employee360.Integration.Persistence;

public class EmployeeIntegrationDbContext : DbContext
{
    public EmployeeIntegrationDbContext(DbContextOptions<EmployeeIntegrationDbContext> options) : base(options) { }

    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();
    public DbSet<EmployeeState> EmployeeStates => Set<EmployeeState>();
    public DbSet<DeadLetter> DeadLetters => Set<DeadLetter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // The primary key on EventId is the hard guarantee behind idempotency:
        // the database itself refuses a second row for the same event.
        modelBuilder.Entity<ProcessedEvent>(e =>
        {
            e.HasKey(x => x.EventId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(x => x.EmployeeId);
        });

        modelBuilder.Entity<EmployeeState>(e => e.HasKey(x => x.EmployeeId));

        modelBuilder.Entity<DeadLetter>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.EventId);
        });
    }
}
