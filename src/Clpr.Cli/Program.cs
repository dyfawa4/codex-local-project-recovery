using System.Text.Json;
using Clpr.Core;

Console.OutputEncoding = System.Text.Encoding.UTF8;
string? Opt(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
string Need(string name) => Opt(name) ?? throw new RecoveryException("缺少 " + name);
void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, State.JsonOptions));
try
{
    var home = Opt("--home") ?? State.DefaultHome;
    switch (args.FirstOrDefault())
    {
        case "inspect": Print(new { Home = home, Environment = State.EnvironmentWarnings(), Running = RecoveryEngine.RunningProcesses(), Snapshots = State.Scan(home) }); break;
        case "compare":
        case "plan":
            var plan = Planner.Create(Need("--source"), Opt("--target") ?? Path.Combine(home, State.LiveName));
            if (Opt("--out") is { } output) File.WriteAllText(output, JsonSerializer.Serialize(plan, State.JsonOptions));
            Print(plan); break;
        case "snapshot": Print(new { Snapshot = RecoveryEngine.Snapshot(Path.Combine(home, State.LiveName), Need("--out")) }); break;
        case "apply":
            var p = JsonSerializer.Deserialize<RecoveryPlan>(File.ReadAllBytes(Need("--plan"))) ?? throw new RecoveryException("计划无效。");
            if (!args.Contains("--yes")) { Console.Write($"恢复 {p.SourceProjects} 项目 / {p.SourceAssignments} 归属到 {p.Target}？输入 y："); if (Console.ReadLine() != "y") return 1; }
            Print(new { Manifest = new RecoveryEngine().Apply(p, Need("--backups"), p.Confirmation) }); break;
        case "verify": var ok = RecoveryEngine.Verify(Need("--manifest")); Print(new { Valid = ok }); return ok ? 0 : 1;
        case "rollback":
            if (!args.Contains("--yes")) { Console.Write("回滚此操作？输入 y："); if (Console.ReadLine() != "y") return 1; }
            new RecoveryEngine().Rollback(Need("--manifest"), true); Print(new { RolledBack = true }); break;
        default:
            Console.WriteLine("CLPR 1.0 · 本地项目恢复\ninspect [--home DIR]\nplan --source FILE [--home DIR | --target FILE] [--out PLAN]\nsnapshot [--home DIR] --out DIR\napply --plan FILE --backups DIR [--yes]\nverify --manifest FILE\nrollback --manifest FILE [--yes]\n默认 inspect/plan 只读。不读取登录凭据。"); break;
    }
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine("未完成：" + ex.Message); return 1; }
