using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Clpr.Core;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;
using SearchOption = System.IO.SearchOption;

namespace Clpr.Desktop;

public partial class MainWindow : Window
{
    public sealed record SnapshotRow(Snapshot Snapshot, string DisplayName)
    {
        public int Projects => Snapshot.Projects;
        public int Assignments => Snapshot.Assignments;
        public string Time => Snapshot.ModifiedUtc == DateTime.MinValue ? "—" : Snapshot.ModifiedUtc.ToLocalTime().ToString("MM-dd HH:mm:ss");
        public string Status => Snapshot.Valid ? Snapshot.Projects > 0 ? "可用" : "项目为空" : "不支持恢复";
    }
    public sealed record HistoryRow(string Manifest, Operation Operation, string Status)
    {
        public string Time => Operation.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        public string Counts => $"{Operation.Projects} / {Operation.Assignments}";
    }

    RecoveryPlan? plan;
    readonly RecoveryEngine engine = new();
    readonly DispatcherTimer stateTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    string currentPage = "home";
    bool initialized;
    bool busy;
    string sourceStamp = "", targetStamp = "";
    string[] running = [];
    string Work => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CLPR");
    string SnapshotRoot => Path.Combine(Work, "snapshots");
    string Target => Path.Combine(HomeBox.Text.Trim(), State.LiveName);
    SnapshotRow? Selected => SnapshotsGrid.SelectedItem as SnapshotRow;

    public MainWindow()
    {
        InitializeComponent();
        HomeBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        EnvironmentText.Text = "默认使用当前 Windows 用户的 .codex 目录；如已迁移数据，请点击“选择目录”。";
        initialized = true;
        SetPage("home");
        stateTimer.Tick += (_, _) => RefreshState();
        Loaded += async (_, _) => { await Scan(); stateTimer.Start(); };
        Closed += (_, _) => stateTimer.Stop();
    }
    static string Stamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "missing";
    }
    void RefreshState()
    {
        if (busy) return;
        try
        {
            running = RecoveryEngine.RunningProcesses();
            ProcessText.Text = running.Length > 0 ? "正在运行" : "已退出";
            if (plan != null && (Stamp(plan.Source) != sourceStamp || Stamp(plan.Target) != targetStamp))
                InvalidatePreview("状态文件发生变化，请重新生成预览。", true);
            UpdateActions();
        }
        catch { /* A transient process exit must not interrupt an interaction. Apply rechecks in Core. */ }
    }
    void SetPage(string page)
    {
        currentPage = page;
        HomePage.Visibility = page == "home" ? Visibility.Visible : Visibility.Collapsed;
        RestorePage.Visibility = page == "restore" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = page == "history" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (button, key) in new[] { (HomeNav, "home"), (RestoreNav, "restore"), (HistoryNav, "history") })
        {
            button.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(page == key ? "#2B4666" : "#16283E"));
            button.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(page == key ? "#FFFFFF" : "#B8C8DB"));
        }
        (PageTitle.Text, PageSubtitle.Text) = page switch
        {
            "restore" => ("恢复项目", "选择快照并生成预览。当前已有项目时，可确认用所选快照覆盖。"),
            "history" => ("操作记录", "每次恢复都会自动备份。需要撤销时，在这里选择对应操作。"),
            _ => ("状态与备份", "项目正常时保存一份快照，下次切号后就有据可恢复。")
        };
    }
    async void NavigateClick(object sender, RoutedEventArgs e)
    {
        var page = (sender as Button)?.Tag?.ToString() ?? "home";
        var enteringRestore = page == "restore" && currentPage != "restore";
        SetPage(page);
        if (page == "history") await LoadHistory();
        else if (enteringRestore && plan == null) await Scan();
    }
    void UpdateActions()
    {
        if (!initialized) return;
        ApplyButton.IsEnabled = !busy && plan?.CanApply == true;
        ApplyButton.Content = plan?.RequiresOverwrite == true ? "覆盖当前项目" : "恢复项目";
        PreviewButton.IsEnabled = !busy && Selected != null;
        DeleteSnapshotButton.IsEnabled = !busy && Selected != null && ManagedSnapshotDirectory(Selected.Snapshot.Path) != null;
        ExportButton.IsEnabled = !busy && plan != null;
        RollbackButton.IsEnabled = !busy && HistoryGrid.SelectedItem is HistoryRow row && row.Status == "可回滚";
        RecoveryHint.Text = plan switch
        {
            { CanApply: true } when running.Length > 0 => "预览已通过。请先完全退出 Codex，再执行操作。",
            { CanApply: true, RequiresOverwrite: true } => "可覆盖。确认后会用快照替换当前项目和聊天归属，覆盖前自动备份。",
            { CanApply: true } => "预览已通过，可以直接恢复。写入前自动备份，可在操作记录中回滚。",
            null => "请先完成预览。写入前自动备份，可在操作记录中回滚。",
            _ => "请查看上方的预览结果。"
        };
    }
    void InvalidatePreview(string message = "选择快照后点击预览，程序会检查差异和项目关联。", bool notify = false)
    {
        plan = null;
        if (!initialized) return;
        PreviewCounts.Text = "尚未生成有效预览";
        PreviewDetails.Text = message;
        TechnicalDetails.Text = "";
        if (notify) StatusText.Text = message;
        UpdateActions();
    }
    void InputsChanged(object sender, TextChangedEventArgs e) => InvalidatePreview("数据目录已改变，请重新扫描并选择快照。", true);
    void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        InvalidatePreview();
        SelectionText.Text = Selected is { } row
            ? $"已选：{row.DisplayName} · {row.Projects} 个项目 / {row.Assignments} 条归属 · {row.Time}"
            : "未找到可用快照时，可点击“导入快照”选择已有备份。";
    }
    async Task Run(string message, Func<Task> action)
    {
        if (busy) return;
        try
        {
            busy = true; IsEnabled = false; StatusText.Text = message;
            await Dispatcher.Yield(DispatcherPriority.Background);
            await action();
        }
        catch (Exception ex) { StatusText.Text = "未完成：" + ex.Message; }
        finally { busy = false; IsEnabled = true; RefreshState(); UpdateActions(); }
    }
    SnapshotRow ToRow(Snapshot s)
    {
        var filename = Path.GetFileName(s.Path);
        var label = ManagedSnapshotDirectory(s.Path) != null ? "已保存快照"
            : filename == State.LiveName + ".bak" ? "Codex 自动备份"
            : filename.Contains(".tmp", StringComparison.OrdinalIgnoreCase) ? "历史临时快照"
            : filename == "account1-good.json" ? "旧版正常备份" : "导入 · " + filename;
        return new SnapshotRow(s, label);
    }
    string? ManagedSnapshotDirectory(string snapshot)
    {
        var dir = Path.GetDirectoryName(snapshot);
        if (dir == null || !Path.GetFileName(snapshot).Equals(State.LiveName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(dir), SnapshotRoot, StringComparison.OrdinalIgnoreCase))
            return null;
        const string prefix = "snapshot-";
        var name = Path.GetFileName(dir);
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(name[prefix.Length..], "N", out _) ? dir : null;
    }

    async void DeleteSnapshotClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } selected || ManagedSnapshotDirectory(selected.Snapshot.Path) is not { } dir) return;
        var message = $"将 {selected.Time} 保存的快照移到回收站？\n\n{selected.Projects} 个项目 / {selected.Assignments} 条聊天归属";
        if (MessageBox.Show(this, message, "删除所选快照", MessageBoxButton.OKCancel,
            MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        await Run("正在清理所选快照…", async () =>
        {
            if (Path.GetFullPath(Target).Equals(selected.Snapshot.Path, StringComparison.OrdinalIgnoreCase))
                throw new RecoveryException("所选快照是当前目标文件，请先切换数据目录。");
            await Task.Run(() =>
            {
                for (var path = dir; path != null; path = Path.GetDirectoryName(path))
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                        throw new RecoveryException("快照目录经过链接，无法清理。");
                foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
                    if ((Path.GetFileName(entry) is not (State.LiveName or "snapshot.json")) ||
                        (File.GetAttributes(entry) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                        throw new RecoveryException("快照目录含有其他文件，请手动检查。");
                FileSystem.DeleteDirectory(dir, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
                if (Directory.Exists(dir)) throw new RecoveryException("快照未移到回收站。");
            });
            var rows = (SnapshotsGrid.ItemsSource as SnapshotRow[] ?? [])
                .Where(row => !row.Snapshot.Path.Equals(selected.Snapshot.Path, StringComparison.OrdinalIgnoreCase)).ToArray();
            SnapshotsGrid.ItemsSource = rows;
            SnapshotsGrid.SelectedItem = rows.FirstOrDefault(row => row.Snapshot.Valid && row.Projects > 0);
            CandidatesText.Text = rows.Count(row => row.Snapshot.Valid && row.Projects > 0).ToString();
            StatusText.Text = "所选快照已移到回收站。";
        });
    }
    async Task Scan()
    {
        await Run("正在扫描本地状态和历史快照…", async () =>
        {
            InvalidatePreview();
            var home = Path.GetFullPath(HomeBox.Text.Trim()); var work = Work;
            var data = await Task.Run(() =>
            {
                var all = State.Scan(home).ToList();
                var saved = Path.Combine(work, "snapshots");
                if (Directory.Exists(saved)) all.AddRange(Directory.EnumerateFiles(saved, State.LiveName, SearchOption.AllDirectories).Select(State.Inspect));
                var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexProjectRecovery", "account1-good.json");
                if (File.Exists(legacy)) all.Add(State.Inspect(legacy));
                return all.DistinctBy(s => s.Path, StringComparer.OrdinalIgnoreCase).OrderByDescending(s => s.Score).ThenByDescending(s => s.ModifiedUtc).ToArray();
            });
            var current = await Task.Run(() => State.Inspect(Path.Combine(home, State.LiveName)));
            CountsText.Text = current.Sha256.Length > 0 ? $"{current.Projects} / {current.Assignments}" : "无法读取";
            var sourceRows = data.Where(s => !s.Path.Equals(Path.GetFullPath(Target), StringComparison.OrdinalIgnoreCase)).Select(ToRow).ToArray();
            CandidatesText.Text = sourceRows.Count(r => r.Snapshot.Valid && r.Projects > 0).ToString();
            SnapshotsGrid.ItemsSource = sourceRows;
            SnapshotsGrid.SelectedItem = sourceRows.FirstOrDefault(r => r.Snapshot.Valid && r.Projects > 0);
            GuidanceTitle.Text = current.Valid && current.Projects > 0 ? "项目列表已存在，先保存正常状态" : current.Valid ? "当前项目列表为空，可以尝试从快照恢复" : "当前状态需要检查";
            GuidanceText.Text = current.Valid && current.Projects > 0
                ? "如果 Codex 中项目都正常，点击下方“保存当前快照”即可。切号后项目仍在就无需恢复；消失时再进入恢复流程。"
                : current.Valid ? "点击“开始恢复”，核对来源快照的数量和日期，预览通过后直接点击恢复。" : string.Join("\n", current.Problems);
            StatusText.Text = $"扫描完成 · 找到 {sourceRows.Length} 份历史状态，其中 {CandidatesText.Text} 份可用。当前文件不作为恢复来源。";
        });
    }
    async void ScanClick(object sender, RoutedEventArgs e) => await Scan();
    async void HomeClick(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "选择 Codex 数据目录" };
        if (d.ShowDialog() == true) { HomeBox.Text = d.FolderName; await Scan(); }
    }
    void SourceClick(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Title = "选择已知正常的 JSON / bak / tmp 快照", Filter = "所有状态文件|*.*" };
        if (d.ShowDialog() != true) return;
        try
        {
            if (Path.GetFullPath(d.FileName).Equals(Path.GetFullPath(Target), StringComparison.OrdinalIgnoreCase))
                throw new RecoveryException("这是当前正在恢复的目标文件，请选择以前保存的正常快照。");
            var row = ToRow(State.Inspect(d.FileName));
            var rows = (SnapshotsGrid.ItemsSource as SnapshotRow[] ?? []).Where(r => r.Snapshot.Path != row.Snapshot.Path).ToList(); rows.Insert(0, row);
            SnapshotsGrid.ItemsSource = rows.ToArray(); SnapshotsGrid.SelectedItem = row;
            StatusText.Text = "已导入快照，请核对数量与日期，然后生成预览。";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    async void PreviewClick(object sender, RoutedEventArgs e)
    {
        await Run("正在检查快照、差异与项目关联…", async () =>
        {
            var source = Selected?.Snapshot.Path ?? throw new RecoveryException("请先选择正常快照。"); var target = Target;
            InvalidatePreview();
            var beforeSource = Stamp(source); var beforeTarget = Stamp(target);
            var result = await Task.Run(() => Planner.Create(source, target, allowOverwrite: true));
            if (Stamp(source) != beforeSource || Stamp(target) != beforeTarget) throw new RecoveryException("预览期间状态变化，请重新预览。");
            plan = result; sourceStamp = beforeSource; targetStamp = beforeTarget;
            PreviewCounts.Text = $"当前 {plan.TargetProjects} 项目 / {plan.TargetAssignments} 归属  →  快照 {plan.SourceProjects} 项目 / {plan.SourceAssignments} 归属";
            PreviewDetails.Text = plan switch
            {
                { CanApply: false } => "暂不恢复：\n" + string.Join("\n", plan.Blockers),
                { RequiresOverwrite: true } => $"项目：新增 {plan.AddedProjects}，替换 {plan.ProjectConflicts}，移除 {plan.RemovedProjects}。\n聊天归属：新增 {plan.AddedAssignments}，替换 {plan.AssignmentConflicts}，移除 {plan.RemovedAssignments}。",
                _ => $"检查通过 · 将恢复 {plan.SourceProjects} 个项目和 {plan.SourceAssignments} 条聊天归属。"
            };
            TechnicalDetails.Text = $"来源：{plan.Source}\n目标：{plan.Target}\n将改变：{string.Join("、", plan.ChangedFields)}\n来源 SHA-256：{plan.SourceHash}\n目标 SHA-256：{plan.TargetHash}\n数据库仅备份，不验证数据库 ID；文件恢复后需检查 Codex 显示。";
            StatusText.Text = plan.CanApply ? "预览检查通过" : "请查看预览结果";
        });
    }
    async void SnapshotClick(object sender, RoutedEventArgs e)
    {
        await Run("正在保存当前正常快照…", async () =>
        {
            var target = Target; var root = Work;
            var saved = await Task.Run(() =>
            {
                var current = State.Inspect(target);
                if (!current.Valid || current.Projects == 0) throw new RecoveryException("当前没有通过校验的项目列表，请先检查状态，不将空列表作为正常备份。");
                return RecoveryEngine.Snapshot(target, Path.Combine(root, "snapshots"));
            });
            var row = ToRow(State.Inspect(saved));
            var rows = (SnapshotsGrid.ItemsSource as SnapshotRow[] ?? []).ToList(); rows.Insert(0, row);
            SnapshotsGrid.ItemsSource = rows.ToArray(); SnapshotsGrid.SelectedItem = row;
            CandidatesText.Text = rows.Count(r => r.Snapshot.Valid && r.Projects > 0).ToString();
            GuidanceTitle.Text = $"快照已保存 · {row.Projects} 个项目 / {row.Assignments} 条归属";
            GuidanceText.Text = "现在可以正常切换账户。切号后项目正常就无需操作；如果项目消失，点击“开始恢复”，这份快照会自动出现在列表中。";
            StatusText.Text = "保存成功 · 未改变 Codex 状态 · 无需手动导入或记住保存路径";
        });
    }
    void ExportClick(object sender, RoutedEventArgs e)
    {
        if (plan == null) return;
        var d = new SaveFileDialog { FileName = "recovery-plan.json", Filter = "恢复计划|*.json" };
        if (d.ShowDialog() != true) return;
        try
        {
            if (Path.GetFullPath(d.FileName).StartsWith(Path.GetFullPath(HomeBox.Text.Trim()).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                throw new RecoveryException("预览请保存在 Codex 数据目录以外。");
            File.WriteAllText(d.FileName, JsonSerializer.Serialize(plan, State.JsonOptions)); StatusText.Text = "已导出预览详情（包含本地路径，请妥善保管）";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    async void ApplyClick(object sender, RoutedEventArgs e)
    {
        if (plan is not { CanApply: true } selected) return;
        if (selected.RequiresOverwrite)
        {
            var message = $"用所选快照覆盖当前项目和聊天归属？\n\n当前：{selected.TargetProjects} 个项目 / {selected.TargetAssignments} 条归属\n覆盖后：{selected.SourceProjects} 个项目 / {selected.SourceAssignments} 条归属\n\n仅存在于当前状态中的 {selected.RemovedProjects} 个项目和 {selected.RemovedAssignments} 条归属将从列表中移除。覆盖前会备份当前状态。";
            if (MessageBox.Show(this, message, "确认覆盖当前项目", MessageBoxButton.OKCancel,
                MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        }
        if (plan != selected) return;
        await Run("正在自动备份并写入，请稍候…", async () =>
        {
            var root = Work;
            var manifest = await Task.Run(() => engine.Apply(selected, Path.Combine(root, "operations"),
                selected.Confirmation, allowOverwrite: selected.RequiresOverwrite));
            InvalidatePreview();
            PreviewCounts.Text = $"已恢复 {selected.SourceProjects} 个项目 / {selected.SourceAssignments} 条聊天归属";
            PreviewDetails.Text = "文件级恢复完成。请重新打开 Codex 检查项目显示。\n备份已自动保存；如需撤销，可前往左侧“操作记录”回滚。";
            TechnicalDetails.Text = "操作记录：" + manifest;
            CountsText.Text = $"{selected.SourceProjects} / {selected.SourceAssignments}";
            StatusText.Text = "恢复完成 · 请打开 Codex 检查项目和聊天分组";
        });
    }
    HistoryRow OperationRow(string manifest)
    {
        var op = RecoveryEngine.ReadOperation(manifest); var dir = Path.GetDirectoryName(manifest)!;
        var rolledBack = Directory.EnumerateFiles(dir, "*rolled-back*.json").Any();
        var status = rolledBack ? "已回滚" : !File.Exists(Path.Combine(dir, "completed.json")) ? "未完成 / 待核对"
            : RecoveryEngine.Verify(manifest) ? "可回滚" : "状态已变化";
        return new HistoryRow(manifest, op, status);
    }
    async Task LoadHistory()
    {
        await Run("正在读取本地操作记录…", async () =>
        {
            var root = Path.Combine(Work, "operations"); var skipped = 0;
            var rows = await Task.Run(() =>
            {
                var list = new List<HistoryRow>();
                if (Directory.Exists(root)) foreach (var manifest in Directory.EnumerateFiles(root, "manifest.json", SearchOption.AllDirectories))
                    try { list.Add(OperationRow(manifest)); } catch { skipped++; }
                return list.OrderByDescending(r => r.Operation.CreatedUtc).ToArray();
            });
            HistoryGrid.ItemsSource = rows; HistoryGrid.SelectedItem = rows.FirstOrDefault();
            HistoryDetails.Text = rows.Length == 0 ? "尚无恢复操作。保存快照后不需要执行恢复；真正恢复过才会生成记录。" : "选择具体操作即可查看备份与回滚状态。";
            StatusText.Text = $"共 {rows.Length} 条操作记录" + (skipped > 0 ? $" · {skipped} 条记录不可读取" : "");
        });
    }
    void HistorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        if (HistoryGrid.SelectedItem is HistoryRow row)
            HistoryDetails.Text = $"{row.Time} · 恢复 {row.Counts} · {row.Status}\n备份：{Path.GetDirectoryName(row.Manifest)}";
        UpdateActions();
    }
    async void RefreshHistoryClick(object sender, RoutedEventArgs e) => await LoadHistory();
    void ImportOperationClick(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Title = "选择恢复操作的 manifest.json", Filter = "操作记录|manifest.json" };
        if (d.ShowDialog() != true) return;
        try
        {
            var row = OperationRow(d.FileName); var rows = (HistoryGrid.ItemsSource as HistoryRow[] ?? []).Where(r => r.Manifest != row.Manifest).ToList();
            rows.Insert(0, row); HistoryGrid.ItemsSource = rows.ToArray(); HistoryGrid.SelectedItem = row;
            StatusText.Text = "已导入操作记录";
        }
        catch (Exception ex) { StatusText.Text = "无法读取操作记录：" + ex.Message; }
    }
    async void RollbackClick(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not HistoryRow row) return;
        if (MessageBox.Show($"回到 {row.Time} 这次恢复之前的状态？", "回滚所选操作", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await Run("正在回滚所选操作…", async () =>
        {
            await Task.Run(() => engine.Rollback(row.Manifest, true)); InvalidatePreview();
            HistoryDetails.Text = "回滚完成，已恢复到此次修复前的原始文件。请重新扫描当前状态。";
            StatusText.Text = "回滚完成 · 返回“状态与备份”重新扫描";
        });
        await LoadHistory();
    }
}
