using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using TZHJ.Core.Enums;

namespace TZHJ.Gateway.Stores;

/// <summary>审计日志一条（= PostgreSQL audit_records 表的行）。</summary>
[Table("audit_records")]
public sealed class AuditRecord
{
    [Key]
    [Column("audit_id")]
    [MaxLength(36)]
    public required string AuditId { get; init; }

    [Column("flow")]
    public required FlowType Flow { get; init; }

    [Column("employee_id")]
    [MaxLength(50)]
    public required string EmployeeId { get; init; }

    [Column("batch_key")]
    [MaxLength(100)]
    public required string BatchKey { get; init; }

    [Column("window_start")]
    public required DateTime WindowStart { get; init; }

    [Column("window_end")]
    public required DateTime WindowEnd { get; init; }

    [Column("target")]
    [MaxLength(20)]
    public required string Target { get; init; }   // SRM / EBS

    [Column("row_count")]
    public required int RowCount { get; init; }

    [Column("submitted_at")]
    public required DateTime SubmittedAt { get; init; }
}
