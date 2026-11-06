using System.Text.Json;
using System.Text.Json.Nodes;

namespace Clpr.Core;

public sealed record RecoveryPlan(string Id, string Source, string Target, string SourceHash, string TargetHash,
    int SourceProjects, int SourceAssignments, int TargetProjects, int TargetAssignments,
    int AddedProjects, int OverlappingProjects, int ProjectConflicts, int AssignmentConflicts,
    string[] ChangedFields, string[] Blockers)
{
    public bool CanApply => Blockers.Length == 0;
    public string Confirmation => $"RESTORE {SourceProjects} PROJECTS";
}

public static class Planner
{
    public static RecoveryPlan Create(string source, string target)
    {
        source = Path.GetFullPath(source); target = Path.GetFullPath(target);
        var s = State.Inspect(source); var t = State.Inspect(target);
        var blockers = s.Problems.Select(e => "来源：" + e).Concat(t.Problems.Select(e => "目标：" + e)).ToList();
        var added = 0; var overlap = 0; var pc = 0; var ac = 0;
        string[] changed = [];
        if (s.Valid && t.Valid)
        {
            var sr = State.Parse(File.ReadAllBytes(source)); var tr = State.Parse(File.ReadAllBytes(target));
            // All reads must correspond to the hashes in the plan.
            if (State.HashFile(source) != s.Sha256 || State.HashFile(target) != t.Sha256) throw new RecoveryException("预览期间文件变化，请重新预览。");
            var sp = (JsonObject)sr[State.Fields[0]]!; var tp = (JsonObject)tr[State.Fields[0]]!;
            added = sp.Count(x => !tp.ContainsKey(x.Key)); overlap = sp.Count - added;
            pc = sp.Count(x => tp.ContainsKey(x.Key) && !JsonNode.DeepEquals(x.Value, tp[x.Key]));
            var sl = (JsonObject)sr[State.Fields[3]]!; var tl = (JsonObject)tr[State.Fields[3]]!;
            ac = sl.Count(x => tl.ContainsKey(x.Key) && !JsonNode.DeepEquals(x.Value, tl[x.Key]));
            changed = State.Fields.Where(k => !JsonNode.DeepEquals(sr[k], tr[k])).ToArray();
            if (tr["projectless-thread-ids"] is JsonArray ids && ids.Any(x => sl.ContainsKey(x!.GetValue<string>())))
                blockers.Add("来源聊天与目标明确无项目的选择冲突。");
        }
        if (s.Projects == 0) blockers.Add("来源没有可恢复项目。");
        if (t.Projects != 0 || t.Assignments != 0) blockers.Add("目标非空：为保护新增数据，当前版本禁止覆盖，请人工比对。");
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase)) blockers.Add("来源与目标不能相同。");
        if (Path.GetFileName(target) != State.LiveName) blockers.Add("目标必须是所选 profile 的 .codex-global-state.json。");
        return new(Guid.NewGuid().ToString("N"), source, target, s.Sha256, t.Sha256, s.Projects, s.Assignments,
            t.Projects, t.Assignments, added, overlap, pc, ac, changed, blockers.Distinct().ToArray());
    }
    public static byte[] Candidate(RecoveryPlan plan)
    {
        if (!plan.CanApply) throw new RecoveryException("计划存在阻止项。");
        var sb = File.ReadAllBytes(plan.Source); var tb = File.ReadAllBytes(plan.Target);
        if (State.Hash(sb) != plan.SourceHash || State.Hash(tb) != plan.TargetHash) throw new RecoveryException("源或目标已变化，冻结哈希不匹配。");
        var source = State.Parse(sb); var target = State.Parse(tb); var before = (JsonObject)target.DeepClone();
        foreach (var key in State.Fields) target[key] = source[key]!.DeepClone();
        var errors = State.Validate(target);
        if (errors.Length > 0) throw new RecoveryException(string.Join("\n", errors));
        foreach (var p in before.Where(p => !State.Fields.Contains(p.Key)))
            if (!JsonNode.DeepEquals(p.Value, target[p.Key])) throw new RecoveryException("意外修改非白名单字段。");
        var candidate = JsonSerializer.SerializeToUtf8Bytes(target, State.JsonOptions);
        State.Parse(candidate);
        return candidate;
    }
}
