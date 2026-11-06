using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Clpr.Core;

public sealed class RecoveryException(string message) : Exception(message);

public sealed record Snapshot(string Path, string Sha256, int Projects, int Assignments,
    bool Valid, string Status, string[] Problems, string[] FieldStates, DateTime ModifiedUtc, int Score);

public static class State
{
    public const string LiveName = ".codex-global-state.json";
    public static readonly string[] Fields = ["local-projects", "project-order",
        "app-server-project-id-by-legacy-project-id-by-host", "thread-project-assignments"];
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public static string HashFile(string path) => Hash(File.ReadAllBytes(path));
    public static JsonObject Parse(byte[] bytes)
    {
        if (bytes.Length > 32 * 1024 * 1024) throw new RecoveryException("文件超过 32 MiB，请人工检查。");
        try
        {
            // Reject duplicate keys rather than silently choosing an arbitrary value.
            using var doc = JsonDocument.Parse(bytes.AsMemory());
            CheckDuplicates(doc.RootElement);
            return JsonNode.Parse(bytes) as JsonObject ?? throw new RecoveryException("JSON 根节点须为对象。");
        }
        catch (JsonException) { throw new RecoveryException("JSON 无效、被截断或含 NUL 填充。"); }
    }
    static void CheckDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in element.EnumerateObject())
            {
                if (!keys.Add(p.Name)) throw new RecoveryException("JSON 包含重复字段。");
                CheckDuplicates(p.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CheckDuplicates(item);
    }
    public static string Kind(JsonObject root, string key) => !root.ContainsKey(key) ? "missing" : root[key] switch
    {
        null => "null", JsonObject o => o.Count == 0 ? "empty object" : "object",
        JsonArray a => a.Count == 0 ? "empty array" : "array", _ => "wrong type"
    };
    public static bool IsString(JsonNode? node, out string value)
    {
        value = "";
        return node is JsonValue v && v.TryGetValue(out value!) && !string.IsNullOrWhiteSpace(value);
    }
    public static string[] Validate(JsonObject root)
    {
        var errors = new List<string>();
        foreach (var key in Fields)
            if (key == "project-order" ? root[key] is not JsonArray : root[key] is not JsonObject)
                errors.Add($"{key}: {Kind(root, key)}；V1 结构不支持。");
        if (errors.Count > 0) return errors.ToArray();
        var projects = (JsonObject)root[Fields[0]]!;
        var links = (JsonObject)root[Fields[3]]!;
        foreach (var p in projects)
        {
            if (string.IsNullOrWhiteSpace(p.Key) || p.Value is not JsonObject meta || meta.Count == 0)
            { errors.Add("项目 ID 或项目元数据无效。"); continue; }
            if (!IsString(meta["name"], out _) || meta["rootPaths"] is not JsonArray paths || paths.Count == 0 || paths.Any(n => !IsString(n, out _)))
                errors.Add("V1 项目须含 name 和非空 rootPaths 字符串数组；请添加适配器后重试。");
            if (meta.ContainsKey("id") && (!IsString(meta["id"], out var mid) || mid != p.Key)) errors.Add("项目内部 ID 与键不一致。");
        }
        var order = (JsonArray)root[Fields[1]]!;
        var ordered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in order)
            if (!IsString(id, out var s) || !projects.ContainsKey(s) || !ordered.Add(s)) errors.Add("项目顺序含未知 ID、重复或错误类型。");
        if (ordered.Count != projects.Count) errors.Add("项目顺序与项目集合不完整对应。");
        foreach (var link in links)
        {
            if (string.IsNullOrWhiteSpace(link.Key) || link.Value is not JsonObject a ||
                !IsString(a["projectKind"], out var kind) || kind != "local" ||
                !IsString(a["projectId"], out var id) || !projects.ContainsKey(id))
                errors.Add("聊天归属包含非本地类型、悬空项目 ID 或错误结构。");
        }
        // Host -> legacy ID -> server ID. Server IDs belong to a different namespace.
        foreach (var host in (JsonObject)root[Fields[2]]!)
            if (string.IsNullOrWhiteSpace(host.Key) || host.Value is not JsonObject map ||
                map.Any(x => string.IsNullOrWhiteSpace(x.Key) || !IsString(x.Value, out _))) errors.Add("app-server ID 映射结构不支持。");
        if (root["sidebar-project-thread-orders"] is { } sidebar)
        {
            if (sidebar is not JsonObject so) errors.Add("侧栏顺序结构不支持。");
            else foreach (var item in so)
                if (!projects.ContainsKey(item.Key) || item.Value is not JsonObject record || record["threadIds"] is not JsonArray tids || tids.Any(x => !IsString(x, out _)))
                    errors.Add("侧栏顺序含未知项目或错误类型。");
        }
        if (root["pinned-project-ids"] is { } pinned && (pinned is not JsonArray pins || pins.Any(x => !IsString(x, out var id) || !projects.ContainsKey(id))))
            errors.Add("置顶项目包含未知 ID 或错误类型。");
        if (root["project-writable-roots"] is { } writable && writable.ToJsonString() is not ("{}" or "[]"))
            errors.Add("非空 project-writable-roots 尚未适配。");
        if (root["projectless-thread-ids"] is { } noProject)
        {
            if (noProject is not JsonArray ids || ids.Any(x => !IsString(x, out _))) errors.Add("projectless-thread-ids 结构不支持。");
            else if (ids.Any(x => links.ContainsKey(x!.GetValue<string>()))) errors.Add("聊天归属与明确无项目的选择冲突。");
        }
        return errors.Distinct().ToArray();
    }
    public static Snapshot Inspect(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var root = Parse(bytes);
            var errors = Validate(root);
            var n = root[Fields[0]] is JsonObject p ? p.Count : 0;
            var m = root[Fields[3]] is JsonObject a ? a.Count : 0;
            return new(System.IO.Path.GetFullPath(path), Hash(bytes), n, m, errors.Length == 0,
                errors.Length > 0 ? "仅诊断 / 结构不支持" : n == 0 ? "结构有效 / 项目为空" : "可用候选 / 待用户核对",
                errors, Fields.Select(k => $"{k}: {Kind(root, k)}").ToArray(), File.GetLastWriteTimeUtc(path), errors.Length == 0 && n > 0 ? 100 : 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RecoveryException)
        { return new(System.IO.Path.GetFullPath(path), "", 0, 0, false, "无法读取 / JSON 无效", [ex.Message], [], DateTime.MinValue, 0); }
    }
    public static string DefaultHome => Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } home
        ? System.IO.Path.GetFullPath(home) : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    public static string[] EnvironmentWarnings()
    {
        var values = new List<string>();
        foreach (var scope in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            var v = Environment.GetEnvironmentVariable("CODEX_HOME", scope);
            if (!string.IsNullOrWhiteSpace(v)) values.Add($"{scope}: {v}");
        }
        return values.ToArray();
    }
    public static Snapshot[] Scan(string home)
    {
        if (!Directory.Exists(home)) throw new RecoveryException("所选目录不存在。");
        return Directory.EnumerateFiles(home, ".codex-global-state.json*")
            .Where(p => !p.EndsWith(".clpr-lock", StringComparison.OrdinalIgnoreCase))
            .Select(Inspect).OrderByDescending(s => s.Score).ThenByDescending(s => s.ModifiedUtc).ToArray();
    }
}
