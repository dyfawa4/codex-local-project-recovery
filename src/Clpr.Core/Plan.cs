using System.Text.Json;
using System.Text.Json.Nodes;

namespace Clpr.Core;

public sealed record RecoveryPlan(string Id, string Source, string Target, string SourceHash, string TargetHash,
    int SourceProjects, int SourceAssignments, int TargetProjects, int TargetAssignments,
    int AddedProjects, int OverlappingProjects, int ProjectConflicts, int AssignmentConflicts,
    string[] ChangedFields, string[] Blockers, int RemovedProjects = 0, int AddedAssignments = 0, int RemovedAssignments = 0)
{
    public bool CanApply => Blockers.Length == 0;
    public bool RequiresOverwrite => TargetProjects > 0 || TargetAssignments > 0;
    public string Confirmation => $"{(RequiresOverwrite ? "OVERWRITE" : "RESTORE")} {SourceProjects} PROJECTS";
}

public static class Planner
{
    internal static readonly string[] ReferenceFields = ["pinned-project-ids", "sidebar-project-thread-orders", "projectless-thread-ids"];

    public static RecoveryPlan Create(string source, string target, bool allowOverwrite = false)
    {
        source = Path.GetFullPath(source); target = Path.GetFullPath(target);
        var s = State.Inspect(source); var t = State.Inspect(target);
        var blockers = s.Problems.Select(e => "来源：" + e).Concat(t.Problems.Select(e => "目标：" + e)).ToList();
        var added = 0; var overlap = 0; var pc = 0; var ac = 0;
        var removed = 0; var addedAssignments = 0; var removedAssignments = 0;
        string[] changed = [];
        var requiresOverwrite = t.Projects > 0 || t.Assignments > 0;
        if (s.Valid && t.Valid)
        {
            var sb = File.ReadAllBytes(source); var tb = File.ReadAllBytes(target);
            if (State.Hash(sb) != s.Sha256 || State.Hash(tb) != t.Sha256) throw new RecoveryException("预览期间文件变化，请重新预览。");
            var sr = State.Parse(sb); var tr = State.Parse(tb);
            var sp = (JsonObject)sr[State.Fields[0]]!; var tp = (JsonObject)tr[State.Fields[0]]!;
            added = sp.Count(x => !tp.ContainsKey(x.Key)); overlap = sp.Count - added;
            removed = tp.Count - overlap;
            pc = sp.Count(x => tp.ContainsKey(x.Key) && !JsonNode.DeepEquals(x.Value, tp[x.Key]));
            var sl = (JsonObject)sr[State.Fields[3]]!; var tl = (JsonObject)tr[State.Fields[3]]!;
            addedAssignments = sl.Count(x => !tl.ContainsKey(x.Key));
            removedAssignments = tl.Count(x => !sl.ContainsKey(x.Key));
            ac = sl.Count(x => tl.ContainsKey(x.Key) && !JsonNode.DeepEquals(x.Value, tl[x.Key]));
            var proposed = ReplaceMetadata(sr, tr, requiresOverwrite && allowOverwrite);
            changed = State.Fields.Concat(ReferenceFields).Where(k => !JsonNode.DeepEquals(proposed[k], tr[k])).ToArray();
            blockers.AddRange(State.Validate(proposed).Select(e => "恢复结果：" + e));
            if (changed.Length == 0) blockers.Add("当前项目数据已与快照一致，无需恢复。");
        }
        if (s.Projects == 0) blockers.Add("来源没有可恢复项目。");
        if (requiresOverwrite && !allowOverwrite) blockers.Add("目标已有项目或聊天归属，请显式选择覆盖。");
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase)) blockers.Add("来源与目标不能相同。");
        if (Path.GetFileName(target) != State.LiveName) blockers.Add("目标必须是所选 profile 的 .codex-global-state.json。");
        return new(Guid.NewGuid().ToString("N"), source, target, s.Sha256, t.Sha256, s.Projects, s.Assignments,
            t.Projects, t.Assignments, added, overlap, pc, ac, changed, blockers.Distinct().ToArray(),
            removed, addedAssignments, removedAssignments);
    }
    static JsonObject ReplaceMetadata(JsonObject source, JsonObject target, bool overwrite)
    {
        var result = (JsonObject)target.DeepClone();
        foreach (var key in State.Fields) result[key] = source[key]!.DeepClone();
        if (!overwrite) return result;

        var projects = (JsonObject)result[State.Fields[0]]!;
        var assignments = (JsonObject)result[State.Fields[3]]!;
        if (result["pinned-project-ids"] is JsonArray pins)
            foreach (var pin in pins.Where(pin => !projects.ContainsKey(pin!.GetValue<string>())).ToArray())
                pins.Remove(pin);
        if (result["sidebar-project-thread-orders"] is JsonObject sidebar)
            foreach (var id in sidebar.Select(item => item.Key).Where(id => !projects.ContainsKey(id)).ToArray())
                sidebar.Remove(id);
        if (result["projectless-thread-ids"] is JsonArray projectless)
            foreach (var id in projectless.Where(id => assignments.ContainsKey(id!.GetValue<string>())).ToArray())
                projectless.Remove(id);
        return result;
    }

    public static byte[] Candidate(RecoveryPlan plan)
    {
        if (!plan.CanApply) throw new RecoveryException("计划存在阻止项。");
        var sb = File.ReadAllBytes(plan.Source); var tb = File.ReadAllBytes(plan.Target);
        if (State.Hash(sb) != plan.SourceHash || State.Hash(tb) != plan.TargetHash) throw new RecoveryException("源或目标已变化，冻结哈希不匹配。");
        var source = State.Parse(sb); var before = State.Parse(tb);
        var target = ReplaceMetadata(source, before, plan.RequiresOverwrite);
        var errors = State.Validate(target);
        if (errors.Length > 0) throw new RecoveryException(string.Join("\n", errors));
        foreach (var p in before.Where(p => !plan.ChangedFields.Contains(p.Key)))
            if (!JsonNode.DeepEquals(p.Value, target[p.Key])) throw new RecoveryException("意外修改非白名单字段。");
        var candidate = JsonSerializer.SerializeToUtf8Bytes(target, State.JsonOptions);
        State.Parse(candidate);
        return candidate;
    }
}
