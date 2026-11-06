using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Clpr.Core;

// Dependency-free executable test suite. Every write is confined to a generated temp profile.
var root = Path.Combine(Path.GetTempPath(), "clpr-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0; var failed = 0;
void Assert(bool condition, string message = "assertion failed") { if (!condition) throw new Exception(message); }
void Throws(Action action) { try { action(); } catch (Exception e) when (e is RecoveryException or IOException or UnauthorizedAccessException) { return; } throw new Exception("expected rejection"); }
JsonObject Data(int n, int m)
{
    var p = new JsonObject(); var order = new JsonArray(); var links = new JsonObject();
    for (int i = 0; i < n; i++) { p[$"p{i}"] = new JsonObject { ["id"] = $"p{i}", ["name"] = $"示例 {i}", ["rootPaths"] = new JsonArray($"D:\\sample\\{i}") }; order.Add($"p{i}"); }
    for (int i = 0; i < m; i++) links[$"t{i}"] = new JsonObject { ["projectKind"] = "local", ["projectId"] = $"p{i % n}" };
    return new JsonObject { [State.Fields[0]] = p, [State.Fields[1]] = order, [State.Fields[2]] = new JsonObject(), [State.Fields[3]] = links,
        ["unrelated"] = new JsonObject { ["nested"] = new JsonArray(1, "中文", null), ["flag"] = true, ["largeNumber"] = 9007199254740993L } };
}
void Write(string file, JsonObject data) => File.WriteAllText(file, data.ToJsonString(State.JsonOptions));
(string Source, string Target, string Backups) Setup(int n = 3, int m = 4, int tn = 0, int tm = 0)
{
    var dir = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
    var source = Path.Combine(dir, "source.json"); var target = Path.Combine(dir, State.LiveName);
    Write(source, Data(n, m)); Write(target, Data(tn, tm));
    return (source, target, Path.Combine(dir, "backups"));
}
void Test(string name, Action body)
{ try { body(); passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); } }
var engine = new RecoveryEngine(() => []);
foreach (var (n, m) in new[] { (1, 1), (22, 46), (37, 99), (6, 10), (200, 321) })
    Test($"dynamic roundtrip {n}/{m}", () => {
        var f = Setup(n, m); var before = State.HashFile(f.Target); var plan = Planner.Create(f.Source, f.Target);
        Assert(plan.CanApply && plan.SourceProjects == n && plan.SourceAssignments == m);
        var manifest = engine.Apply(plan, f.Backups, plan.Confirmation);
        Assert(RecoveryEngine.Verify(manifest)); Assert(State.Inspect(f.Target).Projects == n);
        engine.Rollback(manifest, true); Assert(State.HashFile(f.Target) == before);
    });
Test("zero source blocked", () => { var f = Setup(0, 0); Assert(!Planner.Create(f.Source, f.Target).CanApply); });
Test("nonempty target blocked", () => { var f = Setup(22, 46, 3, 5); Assert(!Planner.Create(f.Source, f.Target).CanApply); });
Test("older smaller source cannot replace new target", () => { var f = Setup(20, 30, 50, 60); Assert(!Planner.Create(f.Source, f.Target).CanApply); });
Test("explicit overwrite restores a more complete snapshot", () =>
{
    var f = Setup(27, 57, 23, 51);
    var original = State.HashFile(f.Target);
    var plan = Planner.Create(f.Source, f.Target, allowOverwrite: true);
    Assert(plan.CanApply && plan.RequiresOverwrite && plan.AddedProjects == 4 && plan.AddedAssignments == 6);
    Throws(() => engine.Apply(plan, f.Backups, plan.Confirmation));
    Assert(State.HashFile(f.Target) == original);
    var manifest = engine.Apply(plan, f.Backups, plan.Confirmation, allowOverwrite: true);
    Assert(RecoveryEngine.Verify(manifest) && State.Inspect(f.Target).Assignments == 57);
    engine.Rollback(manifest, true);
    Assert(State.HashFile(f.Target) == original);
});
Test("overwrite a smaller snapshot and remove stale references", () =>
{
    var f = Setup(2, 3, 3, 4);
    var target = Data(3, 4);
    target["pinned-project-ids"] = new JsonArray("p0", "p2");
    target["sidebar-project-thread-orders"] = new JsonObject
    {
        ["p0"] = new JsonObject { ["threadIds"] = new JsonArray("t0") },
        ["p2"] = new JsonObject { ["threadIds"] = new JsonArray("t2") }
    };
    Write(f.Target, target);
    var original = State.HashFile(f.Target);
    var plan = Planner.Create(f.Source, f.Target, allowOverwrite: true);
    Assert(plan.CanApply && plan.RemovedProjects == 1 && plan.RemovedAssignments == 1);
    var manifest = engine.Apply(plan, f.Backups, plan.Confirmation, allowOverwrite: true);
    var actual = State.Parse(File.ReadAllBytes(f.Target));
    Assert(RecoveryEngine.Verify(manifest));
    Assert(JsonNode.DeepEquals(actual["pinned-project-ids"], new JsonArray("p0")));
    Assert(((JsonObject)actual["sidebar-project-thread-orders"]!).ContainsKey("p0"));
    Assert(!((JsonObject)actual["sidebar-project-thread-orders"]!).ContainsKey("p2"));
    Assert(JsonNode.DeepEquals(target["unrelated"], actual["unrelated"]));
    engine.Rollback(manifest, true);
    Assert(State.HashFile(f.Target) == original);
});
Test("overwrite reconciles a restored chat previously marked projectless", () =>
{
    var f = Setup(3, 4, 1, 1);
    var target = Data(1, 1);
    target["projectless-thread-ids"] = new JsonArray("t1", "other-thread");
    Write(f.Target, target);
    var plan = Planner.Create(f.Source, f.Target, allowOverwrite: true);
    Assert(plan.CanApply && plan.ChangedFields.Contains("projectless-thread-ids"));
    engine.Apply(plan, f.Backups, plan.Confirmation, allowOverwrite: true);
    var actual = State.Parse(File.ReadAllBytes(f.Target));
    Assert(JsonNode.DeepEquals(actual["projectless-thread-ids"], new JsonArray("other-thread")));
});
Test("overwrite checks original target hash and confirmation", () =>
{
    var f = Setup(3, 4, 1, 1);
    var plan = Planner.Create(f.Source, f.Target, allowOverwrite: true);
    Throws(() => engine.Apply(plan, f.Backups, "RESTORE 3 PROJECTS", allowOverwrite: true));
    File.AppendAllText(f.Target, " ");
    var newerHash = State.HashFile(f.Target);
    Throws(() => engine.Apply(plan, f.Backups, plan.Confirmation, allowOverwrite: true));
    Assert(State.HashFile(f.Target) == newerHash);
});
Test("overwrite blocks identical and invalid project data", () =>
{
    var f = Setup(3, 4, 3, 4);
    Assert(!Planner.Create(f.Source, f.Target, allowOverwrite: true).CanApply);
    var bad = Data(3, 4);
    bad[State.Fields[3]]!["t0"]!["projectId"] = "absent";
    Write(f.Source, bad);
    Assert(!Planner.Create(f.Source, f.Target, allowOverwrite: true).CanApply);
});
foreach (var invalid in new[] { "null", "[]", "{}", "{", "", "\0\0", "{\"x\":1,\"x\":2}" })
    Test("invalid JSON/structure " + JsonSerializer.Serialize(invalid), () => { var f = Setup(); File.WriteAllText(f.Source, invalid); Assert(!State.Inspect(f.Source).Valid); Assert(!Planner.Create(f.Source, f.Target).CanApply); });
Test("missing vs null vs wrong type", () => {
    var d = Data(0, 0); d.Remove(State.Fields[0]); Assert(State.Kind(d, State.Fields[0]) == "missing");
    d[State.Fields[0]] = null; Assert(State.Kind(d, State.Fields[0]) == "null"); d[State.Fields[0]] = new JsonArray(); Assert(State.Kind(d, State.Fields[0]) == "empty array");
    Assert(State.Validate(d).Length > 0);
});
Test("dangling assignment", () => { var f = Setup(); var d = Data(3, 4); d[State.Fields[3]]!["t0"]!["projectId"] = "absent"; Write(f.Source, d); Assert(!Planner.Create(f.Source, f.Target).CanApply); });
Test("cloud assignment rejected", () => { var d = Data(1, 1); d[State.Fields[3]]!["t0"]!["projectKind"] = "cloud"; Assert(State.Validate(d).Length > 0); });
Test("duplicate project order rejected", () => { var d = Data(1, 1); ((JsonArray)d[State.Fields[1]]!).Add("p0"); Assert(State.Validate(d).Length > 0); });
Test("project internal id mismatch", () => { var d = Data(1, 1); d[State.Fields[0]]!["p0"]!["id"] = "other"; Assert(State.Validate(d).Length > 0); });
Test("server map allows separate namespace", () => { var d = Data(1, 1); d[State.Fields[2]] = new JsonObject { ["host"] = new JsonObject { ["legacy"] = "server-id" } }; Assert(State.Validate(d).Length == 0); });
Test("server map invalid leaf", () => { var d = Data(1, 1); d[State.Fields[2]] = new JsonObject { ["host"] = 42 }; Assert(State.Validate(d).Length > 0); });
Test("projectless conflicts", () => { var f = Setup(); var d = Data(0, 0); d["projectless-thread-ids"] = new JsonArray("t0"); Write(f.Target, d); Assert(!Planner.Create(f.Source, f.Target).CanApply); });
Test("process blocks before write", () => { var f = Setup(); var p = Planner.Create(f.Source, f.Target); var h = State.HashFile(f.Target); Throws(() => new RecoveryEngine(() => ["codex"]).Apply(p, f.Backups, p.Confirmation)); Assert(State.HashFile(f.Target) == h); });
Test("target changed after plan", () => { var f = Setup(); var p = Planner.Create(f.Source, f.Target); File.AppendAllText(f.Target, " "); Throws(() => engine.Apply(p, f.Backups, p.Confirmation)); });
Test("source changed after plan", () => { var f = Setup(); var p = Planner.Create(f.Source, f.Target); Write(f.Source, Data(4, 5)); Throws(() => engine.Apply(p, f.Backups, p.Confirmation)); });
Test("target changes during backup", () => { var f = Setup(); var p = Planner.Create(f.Source, f.Target); var e = new RecoveryEngine(() => []); e.BeforeReplaceForTest = () => File.AppendAllText(f.Target, " "); Throws(() => e.Apply(p, f.Backups, p.Confirmation)); Assert(State.Inspect(f.Target).Projects == 0); });
Test("post replace failure auto rollback exact bytes", () => { var f = Setup(); var p = Planner.Create(f.Source, f.Target); var h = State.HashFile(f.Target); var e = new RecoveryEngine(() => []); e.AfterReplaceForTest = () => throw new IOException("injected verify failure"); Throws(() => e.Apply(p, f.Backups, p.Confirmation)); Assert(State.HashFile(f.Target) == h); });
Test("later change prevents auto rollback", () => { var f = Setup(); var p = Planner.Create(f.Source, f.Target); var e = new RecoveryEngine(() => []); e.AfterReplaceForTest = () => { Write(f.Target, Data(5, 6)); throw new IOException("another writer"); }; Throws(() => e.Apply(p, f.Backups, p.Confirmation)); Assert(State.Inspect(f.Target).Projects == 5); });
Test("only four fields and credentials/db unchanged", () => {
    var f = Setup(); var home = Path.GetDirectoryName(f.Target)!;
    foreach (var name in new[] { "auth.json", "config.toml", "state_5.sqlite", "state_5.sqlite-wal", "state_5.sqlite-shm", State.LiveName + ".bak" }) File.WriteAllText(Path.Combine(home, name), "opaque-secret-or-data");
    var before = State.Parse(File.ReadAllBytes(f.Target)); var p = Planner.Create(f.Source, f.Target); var manifest = engine.Apply(p, f.Backups, p.Confirmation);
    var after = State.Parse(File.ReadAllBytes(f.Target)); Assert(JsonNode.DeepEquals(before["unrelated"], after["unrelated"]));
    foreach (var name in new[] { "auth.json", "config.toml", "state_5.sqlite", "state_5.sqlite-wal", "state_5.sqlite-shm", State.LiveName + ".bak" }) Assert(File.ReadAllText(Path.Combine(home, name)) == "opaque-secret-or-data");
    Assert(!File.Exists(Path.Combine(Path.GetDirectoryName(manifest)!, "auth.json")));
    Assert(File.Exists(Path.Combine(Path.GetDirectoryName(manifest)!, "state_5.sqlite-wal")));
});
Test("rollback refuses later state", () => { var f = Setup(); var p = Planner.Create(f.Source, f.Target); var m = engine.Apply(p, f.Backups, p.Confirmation); File.AppendAllText(f.Target, " "); Throws(() => engine.Rollback(m, true)); });
Test("rollback refuses corrupt backup", () => { var f = Setup(); var p = Planner.Create(f.Source, f.Target); var m = engine.Apply(p, f.Backups, p.Confirmation); File.AppendAllText(Path.Combine(Path.GetDirectoryName(m)!, State.LiveName), " "); Throws(() => engine.Rollback(m, true)); });
Test("approval required", () => { var f = Setup(); var p = Planner.Create(f.Source, f.Target); Throws(() => engine.Apply(p, f.Backups, "")); });
Test("repeat apply prevented", () => { var f = Setup(); var p = Planner.Create(f.Source, f.Target); engine.Apply(p, f.Backups, p.Confirmation); Throws(() => engine.Apply(p, f.Backups, p.Confirmation)); });
Test("read-only scan and compare no file changes", () => { var f = Setup(); var before = Directory.GetFiles(Path.GetDirectoryName(f.Target)!).ToDictionary(p => p, State.HashFile); State.Scan(Path.GetDirectoryName(f.Target)!); Planner.Create(f.Source, f.Target); Assert(before.All(x => State.HashFile(x.Key) == x.Value)); Assert(Directory.GetFiles(Path.GetDirectoryName(f.Target)!).Length == before.Count); });
Test("snapshot immutable unique", () => { var f = Setup(); var a = RecoveryEngine.Snapshot(f.Source, f.Backups); var b = RecoveryEngine.Snapshot(f.Source, f.Backups); Assert(a != b && State.HashFile(a) == State.HashFile(f.Source)); });
Test("project metadata conflicts counted", () => { var f = Setup(3, 4, 1, 1); var d = Data(1, 1); d[State.Fields[0]]!["p0"]!["name"] = "changed"; Write(f.Target, d); var p = Planner.Create(f.Source, f.Target); Assert(!p.CanApply && p.ProjectConflicts == 1 && p.OverlappingProjects == 1); });
Test("A/B repeated switching remains dynamic", () => { var f = Setup(5, 8); for (int i = 0; i < 3; i++) { Write(f.Source, Data(5 + i, 8 + i)); Write(f.Target, Data(0, 0)); var p = Planner.Create(f.Source, f.Target); engine.Apply(p, f.Backups, p.Confirmation); Assert(State.Inspect(f.Target).Projects == 5 + i); } });
Console.WriteLine($"RESULT: {passed} passed, {failed} failed. Temp evidence: {root}");
return failed == 0 ? 0 : 1;
