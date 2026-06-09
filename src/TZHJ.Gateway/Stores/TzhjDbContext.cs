using Microsoft.EntityFrameworkCore;

namespace TZHJ.Gateway.Stores;

public sealed class TzhjDbContext : DbContext
{
    public TzhjDbContext(DbContextOptions<TzhjDbContext> options) : base(options)
    {
    }

    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // 可以额外添加复合索引优化 Find() 查询
        modelBuilder.Entity<AuditRecord>()
            .HasIndex(r => new { r.Flow, r.EmployeeId, r.WindowStart, r.WindowEnd })
            .HasDatabaseName("idx_audit_lookup");
    }
}
