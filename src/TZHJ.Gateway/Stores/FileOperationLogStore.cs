using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TZHJ.Core.Contracts.Http;

namespace TZHJ.Gateway.Stores;

/// <summary>操作日志存储路径选项（绑定 appsettings 的 "OperationLog" 节）。</summary>
public sealed class OperationLogOptions
{
    /// <summary>日志目录。相对路径相对网关工作目录。默认 "logs"。</summary>
    public string Directory { get; set; } = "logs";
}

/// <summary>
/// 操作日志文件实现：按月一个文件（operations-yyyyMM.log），每行一条 JSON（JSONL）。
/// 管理员直接打开文件即可查看所有电脑的操作；查询本人记录时全量扫描并按工号过滤
/// （操作量很小——只记回传/补回传，扫描足够）。重启不丢（落盘）。
/// </summary>
public sealed class FileOperationLogStore : IOperationLogStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _dir;
    private readonly object _writeLock = new();

    public FileOperationLogStore(OperationLogOptions options)
    {
        _dir = Path.IsPathRooted(options.Directory)
            ? options.Directory
            : Path.Combine(AppContext.BaseDirectory, options.Directory);
        System.IO.Directory.CreateDirectory(_dir);
    }

    public void Append(OperationLogEntry entry)
    {
        var line = JsonSerializer.Serialize(entry, Json);
        var file = FilePathFor(entry.OperatedAt);
        // 追加单行；锁串行化避免并发写交错。本地小规模 IO，足够。
        lock (_writeLock)
            File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
    }

    public IReadOnlyList<OperationLogEntry> ListByEmployee(string employeeId)
    {
        var result = new List<OperationLogEntry>();
        if (!System.IO.Directory.Exists(_dir)) return result;

        foreach (var file in System.IO.Directory.EnumerateFiles(_dir, "operations-*.log"))
        {
            string[] lines;
            lock (_writeLock) lines = File.ReadAllLines(file, Encoding.UTF8);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                OperationLogEntry? entry;
                try { entry = JsonSerializer.Deserialize<OperationLogEntry>(line, Json); }
                catch { continue; } // 跳过损坏行，不影响其余
                if (entry is not null && entry.EmployeeId == employeeId) result.Add(entry);
            }
        }

        return result.OrderByDescending(e => e.OperatedAt).ToList();
    }

    private string FilePathFor(DateTime when) =>
        Path.Combine(_dir, $"operations-{when:yyyyMM}.log");
}
