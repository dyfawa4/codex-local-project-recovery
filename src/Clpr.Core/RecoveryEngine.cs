using System.Diagnostics;
using System.Text.Json;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Clpr.Tests")]
namespace Clpr.Core;

public sealed record Operation(string Id, string Target, string BeforeHash, string AfterHash, string[] Fields,
    int Projects, int Assignments, DateTime CreatedUtc, Dictionary<string, string> BackupHashes, string ToolVersion = "1.1.0");

public sealed class RecoveryEngine
{
    readonly Func<string[]> processes;
    internal Action? BeforeReplaceForTest;
    internal Action? AfterReplaceForTest;
    public RecoveryEngine() : this(RunningProcesses) { }
    internal RecoveryEngine(Func<string[]> processes) => this.processes = processes;
    public static string[] RunningProcesses()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    var n = p.ProcessName;
                    // ChatGPT alone is not evidence of a Codex process.
                    if (n.Equals("codex", StringComparison.OrdinalIgnoreCase) || n.StartsWith("codex-", StringComparison.OrdinalIgnoreCase))
                        names.Add($"{n} (PID {p.Id})");
                }
                catch (InvalidOperationException) { }
            }
        }
        return names.Order().ToArray();
    }
    void Closed()
    {
        var running = processes();
        if (running.Length > 0) throw new RecoveryException("请完全退出 Codex 后再恢复。检测到：" + string.Join(", ", running));
    }
    static void SafePath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\")) throw new RecoveryException("写入暂不支持网络路径。");
        for (var p = full; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new RecoveryException("写入暂不支持符号链接/重解析路径。");
    }
    static void WriteNew(string path, byte[] bytes)
    {
        using var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        f.Write(bytes); f.Flush(true);
    }
    static void Save<T>(string path, T value) => WriteNew(path, JsonSerializer.SerializeToUtf8Bytes(value, State.JsonOptions));
    static void Expect(string path, string hash)
    { if (State.HashFile(path) != hash) throw new RecoveryException("文件已变化或备份被修改；请重新扫描预览。"); }
    static FileStream Lock(string target) => new(target + ".clpr-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
    static void Replace(string target, byte[] payload, string displaced)
    {
        var temp = target + ".clpr-" + Guid.NewGuid().ToString("N");
        try
        {
            WriteNew(temp, payload);
            Expect(temp, State.Hash(payload));
            // ReplaceFile preserves destination ACL on Windows; metadata errors must fail.
            File.Replace(temp, target, displaced, false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public string Apply(RecoveryPlan plan, string backupRoot, string confirmation, bool allowOverwrite = false)
    {
        if (confirmation != plan.Confirmation) throw new RecoveryException("尚未确认此预览。");
        if (!plan.CanApply) throw new RecoveryException(string.Join("\n", plan.Blockers));
        SafePath(plan.Target); SafePath(plan.Source); SafePath(backupRoot); Closed();
        using var mutex = Lock(plan.Target);
        var fresh = Planner.Create(plan.Source, plan.Target, allowOverwrite);
        if (fresh.RequiresOverwrite && !allowOverwrite)
            throw new RecoveryException("目标已有项目或聊天归属，写入前需要确认覆盖。");
        if (!fresh.CanApply || fresh.SourceHash != plan.SourceHash || fresh.TargetHash != plan.TargetHash)
            throw new RecoveryException("计划已过期或不再可用，请重新预览。");
        if (confirmation != fresh.Confirmation || fresh.RequiresOverwrite != plan.RequiresOverwrite)
            throw new RecoveryException("确认与当前计划不一致，请重新预览。");
        var payload = Planner.Candidate(fresh);
        var afterHash = State.Hash(payload);
        var dir = Path.Combine(Path.GetFullPath(backupRoot), DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var home = Path.GetDirectoryName(plan.Target)!;
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new[] { State.LiveName, State.LiveName + ".bak" }
            .Concat(Directory.EnumerateFiles(home, "state_*.sqlite*").Select(Path.GetFileName)!)
            .Where(n => n != null && (n.EndsWith(".sqlite") || n.EndsWith(".sqlite-wal") || n.EndsWith(".sqlite-shm") || n.StartsWith(State.LiveName)))
            .Distinct().ToArray();
        foreach (var name in names)
        {
            var file = Path.Combine(home, name!);
            if (!File.Exists(file)) continue;
            SafePath(file);
            var bytes = File.ReadAllBytes(file);
            WriteNew(Path.Combine(dir, name!), bytes);
            hashes[name!] = State.Hash(bytes);
            Expect(file, hashes[name!]); Expect(Path.Combine(dir, name!), hashes[name!]);
        }
        if (!hashes.TryGetValue(State.LiveName, out var original) || original != plan.TargetHash) throw new RecoveryException("备份目标哈希不匹配。");
        var fields = State.Fields.Concat(fresh.ChangedFields.Where(Planner.ReferenceFields.Contains)).ToArray();
        var op = new Operation(Path.GetFileName(dir), plan.Target, plan.TargetHash, afterHash, fields,
            fresh.SourceProjects, fresh.SourceAssignments, DateTime.UtcNow, hashes);
        WriteNew(Path.Combine(dir, "candidate.json"), payload);
        Save(Path.Combine(dir, "manifest.json"), op); // Durable recovery record BEFORE touching live.
        WriteNew(Path.Combine(dir, "回滚说明.txt"), System.Text.Encoding.UTF8.GetBytes(
            "在工具中选择此目录的 manifest.json，退出 Codex 后点击回滚。仅当当前哈希与本次写入一致时允许回滚。备份可能包含敏感路径与数据库，请本地妥善保管。"));
        BeforeReplaceForTest?.Invoke();
        Closed(); Expect(plan.Source, plan.SourceHash); Expect(plan.Target, plan.TargetHash);
        foreach (var pair in hashes) Expect(Path.Combine(home, pair.Key), pair.Value);
        var displaced = Path.Combine(dir, "atomic-before.json");
        try
        {
            Replace(plan.Target, payload, displaced);
            AfterReplaceForTest?.Invoke();
            Expect(displaced, plan.TargetHash);
            Expect(plan.Target, afterHash);
            var verified = State.Inspect(plan.Target);
            if (!verified.Valid || verified.Projects != op.Projects || verified.Assignments != op.Assignments) throw new RecoveryException("写后校验失败。");
            Save(Path.Combine(dir, "completed.json"), new { op.Id, op.AfterHash, VerifiedUtc = DateTime.UtcNow });
            return Path.Combine(dir, "manifest.json");
        }
        catch
        {
            // Never overwrite somebody else's later write. Displaced bytes are authoritative on a race.
            if (File.Exists(displaced) && State.HashFile(plan.Target) == afterHash)
            {
                Closed();
                Replace(plan.Target, File.ReadAllBytes(displaced), Path.Combine(dir, "failed-candidate.json"));
                Save(Path.Combine(dir, "auto-rolled-back.json"), new { TimeUtc = DateTime.UtcNow });
            }
            throw;
        }
    }
    public static Operation ReadOperation(string manifest)
    {
        var op = JsonSerializer.Deserialize<Operation>(File.ReadAllBytes(manifest)) ?? throw new RecoveryException("操作记录无效。");
        if (Path.GetFileName(op.Target) != State.LiveName || !op.Fields.Take(State.Fields.Length).SequenceEqual(State.Fields) ||
            op.Fields.Distinct().Count() != op.Fields.Length || op.Fields.Skip(State.Fields.Length).Any(f => !Planner.ReferenceFields.Contains(f)) ||
            op.BeforeHash.Length != 64 || op.AfterHash.Length != 64) throw new RecoveryException("操作记录结构无效。");
        return op;
    }
    public static bool Verify(string manifest)
    {
        var op = ReadOperation(manifest);
        return State.HashFile(op.Target) == op.AfterHash && State.Inspect(op.Target).Valid;
    }
    public void Rollback(string manifest, bool confirmed)
    {
        if (!confirmed) throw new RecoveryException("尚未确认回滚。");
        var op = ReadOperation(manifest);
        SafePath(op.Target); SafePath(manifest); Closed();
        var dir = Path.GetDirectoryName(Path.GetFullPath(manifest))!;
        var backup = Path.Combine(dir, State.LiveName);
        SafePath(backup);
        using var mutex = Lock(op.Target);
        Expect(op.Target, op.AfterHash); Expect(backup, op.BeforeHash);
        var original = File.ReadAllBytes(backup); State.Parse(original);
        Closed(); Expect(op.Target, op.AfterHash);
        var receipt = Guid.NewGuid().ToString("N");
        Replace(op.Target, original, Path.Combine(dir, "before-rollback-" + receipt + ".json"));
        Expect(op.Target, op.BeforeHash);
        Save(Path.Combine(dir, "rolled-back-" + receipt + ".json"), new { TimeUtc = DateTime.UtcNow, op.BeforeHash });
    }
    public static string Snapshot(string source, string outputRoot)
    {
        var bytes = File.ReadAllBytes(source); State.Parse(bytes);
        var dir = Path.Combine(outputRoot, "snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, State.LiveName);
        WriteNew(path, bytes);
        Save(Path.Combine(dir, "snapshot.json"), new { Sha256 = State.Hash(bytes), CapturedUtc = DateTime.UtcNow });
        return path;
    }
}
