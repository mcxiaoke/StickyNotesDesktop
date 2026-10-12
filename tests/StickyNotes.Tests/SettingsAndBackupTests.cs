using System.IO;
using System.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StickyNotes.Data;
using StickyNotes.Infrastructure;
using StickyNotes.Messages;
using StickyNotes.Models;
using StickyNotes.Services;
using StickyNotes.ViewModels;

namespace StickyNotes.Tests;

[TestClass]
public class SettingsAndBackupTests
{
    private string _testDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"StickyNotes_Test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, true);
        }
        catch { }
    }

    [TestMethod]
    public void SettingsService_DefaultAndPersistence_ShouldWork()
    {
        var settingsFile = Path.Combine(_testDir, "settings.json");
        var service = new SettingsService(settingsFile);

        // 默认字号为 14.0
        Assert.AreEqual(14.0, service.EditorFontSize);

        // 修改并自动保存
        service.SetEditorFontSize(18.0);
        Assert.AreEqual(18.0, service.EditorFontSize);
        Assert.IsTrue(File.Exists(settingsFile));

        // 重新加载验证持久化
        var reloadedService = new SettingsService(settingsFile);
        Assert.AreEqual(18.0, reloadedService.EditorFontSize);

        // 超界保护（10 ~ 36）
        service.SetEditorFontSize(5.0);
        Assert.AreEqual(10.0, service.EditorFontSize);

        service.SetEditorFontSize(50.0);
        Assert.AreEqual(36.0, service.EditorFontSize);
    }

    [TestMethod]
    public async Task ExportImportService_RoundTrip_ShouldPreserveNotes()
    {
        var dbPath = Path.Combine(_testDir, "test.db");
        var dbContext = new SqliteDatabaseContext(dbPath);
        await dbContext.InitializeAndMigrateAsync();
        var repo = new NoteRepository(dbContext);
        var backupService = new ExportImportService(repo);

        var note1 = new Note
        {
            Id = Guid.NewGuid(),
            Content = "这是待备份便签 1",
            Color = NoteColor.Blue,
            IsPinned = true,
            WindowX = 200,
            WindowY = 220,
            WindowWidth = 380,
            WindowHeight = 420
        };

        var note2 = new Note
        {
            Id = Guid.NewGuid(),
            Content = "这是已归档便签 2",
            Color = NoteColor.Purple,
            IsDeleted = true
        };

        await repo.SaveAsync(note1);
        await repo.SaveAsync(note2);

        var exportFile = Path.Combine(_testDir, "backup.json");
        int exportedCount = await backupService.ExportNotesAsync(exportFile);

        Assert.AreEqual(2, exportedCount);
        Assert.IsTrue(File.Exists(exportFile));

        // 模拟还原到全新干净的数据库
        var newDbPath = Path.Combine(_testDir, "restored.db");
        var newDbContext = new SqliteDatabaseContext(newDbPath);
        await newDbContext.InitializeAndMigrateAsync();
        var newRepo = new NoteRepository(newDbContext);
        var newBackupService = new ExportImportService(newRepo);

        int importedCount = await newBackupService.ImportNotesAsync(exportFile);
        Assert.AreEqual(2, importedCount);

        var restoredActive = await newRepo.GetAllActiveAsync();
        var restoredArchived = await newRepo.GetAllArchivedAsync();

        Assert.AreEqual(1, restoredActive.Count);
        Assert.AreEqual(note1.Id, restoredActive[0].Id);
        Assert.AreEqual("这是待备份便签 1", restoredActive[0].Content);
        Assert.AreEqual(NoteColor.Blue, restoredActive[0].Color);
        Assert.IsTrue(restoredActive[0].IsPinned);

        Assert.AreEqual(1, restoredArchived.Count);
        Assert.AreEqual(note2.Id, restoredArchived[0].Id);
        Assert.AreEqual("这是已归档便签 2", restoredArchived[0].Content);
        Assert.AreEqual(NoteColor.Purple, restoredArchived[0].Color);
        Assert.IsTrue(restoredArchived[0].IsDeleted);
    }

    /// <summary>
    /// F-P1-6 验证：settings.json 采用原子写入 —— 写入后目标文件内容完整可解析，
    /// 且首次写入不残留 .tmp；覆盖写入时生成 .bak 备份。
    /// </summary>
    [TestMethod]
    public void F_P1_6_SaveSettings_WritesAtomically()
    {
        var settingsPath = Path.Combine(_testDir, "settings.json");

        var service = new SettingsService(settingsPath);
        service.SetEditorFontSize(18.0);

        Assert.IsTrue(File.Exists(settingsPath), "目标设置文件必须存在");
        Assert.IsFalse(File.Exists(settingsPath + ".tmp"), "写入完成后不得残留 .tmp 临时文件");

        // 内容必须是完整可解析的 JSON（原子替换的核心保证）
        var json = File.ReadAllText(settingsPath);
        var parsed = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);
        Assert.IsNotNull(parsed, "设置文件必须是合法的完整 JSON");
        Assert.AreEqual(18.0, parsed.EditorFontSize, 0.001, "写入的设置值必须正确落盘");

        // 第二次覆盖写入应生成 .bak 备份
        service.SetEditorFontSize(20.0);
        Assert.IsTrue(File.Exists(settingsPath + ".bak"), "覆盖写入时应保留 .bak 备份");

        var reparsed = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsPath));
        Assert.IsNotNull(reparsed);
        Assert.AreEqual(20.0, reparsed.EditorFontSize, 0.001);
    }

    /// <summary>
    /// F-P1-6 验证：settings.json 中 PinEnabled 为真但盐/哈希缺失时，
    /// 不得静默降级为「无 PIN」，必须标记 PinDataCorrupted 以便向用户显式告警。
    /// </summary>
    [TestMethod]
    public void F_P1_6_CorruptedPinData_IsFlaggedInsteadOfSilentlyDisabled()
    {
        var settingsPath = Path.Combine(_testDir, "settings_corrupt.json");
        File.WriteAllText(settingsPath, """
            {
              "EditorFontSize": 14.0,
              "MinimizeToTrayOnClose": true,
              "EnableGlobalHotKeys": true,
              "StartMinimized": true,
              "PinEnabled": true,
              "PinSalt": "",
              "PinHash": "",
              "IdleLockMinutes": 10,
              "LockOnLaunch": true
            }
            """);

        var service = new SettingsService(settingsPath);

        Assert.IsFalse(service.Settings.PinEnabled, "损坏的 PIN 数据必须使锁定失效，避免假安全");
        Assert.IsTrue(service.Settings.PinDataCorrupted, "必须标记 PinDataCorrupted 以便 UI 显式告警用户");

        // PinService 层面也必须一致：不生效
        var pinService = new PinService(service);
        Assert.IsFalse(pinService.IsPinEnabled, "PIN 数据损坏时 IsPinEnabled 必须为 false");
        Assert.IsFalse(pinService.IsPinSet, "盐/哈希缺失时 IsPinSet 必须为 false");
    }

    /// <summary>
    /// F-P1-6 验证：settings.json 解析失败时应回退到 .bak，而不是丢失用户的全部设置
    /// </summary>
    [TestMethod]
    public void F_P1_6_BrokenSettingsJson_FallsBackToBackup()
    {
        var settingsPath = Path.Combine(_testDir, "settings_recover.json");

        // 先用正常流程生成一份有效设置与 .bak
        var service = new SettingsService(settingsPath);
        service.SetEditorFontSize(16.0);
        service.SetEditorFontSize(22.0);
        Assert.IsTrue(File.Exists(settingsPath + ".bak"), "前置条件：应存在 .bak 备份");

        // 主文件被写坏（模拟断电留下半截 JSON）
        File.WriteAllText(settingsPath, "{ \"EditorFontSize\": 16.0, \"PinEn");

        var recovered = new SettingsService(settingsPath);
        Assert.IsNotNull(recovered.Settings);
        // 从 .bak 恢复后应拿到备份时刻的合法字号，而不是静默回退默认值 14.0
        Assert.AreEqual(16.0, recovered.Settings.EditorFontSize, 0.001,
            "主文件损坏时应从 .bak 恢复用户设置，而非静默回退默认值");
    }

    [TestMethod]
    public void F_P2_24_ExpiredLogFiles_AreIdentifiedForCleanup()
    {
        var now = new DateTime(2026, 10, 3, 12, 0, 0);

        // 超过保留期（31 天前）→ 应被清理
        Assert.IsTrue(AppLog.IsExpiredLogFile(Path.Combine(_testDir, "app-20260901.log"), now),
            "31 天前的日志应判定为过期");

        // 保留期内（29 天前）→ 不应清理
        Assert.IsFalse(AppLog.IsExpiredLogFile(Path.Combine(_testDir, "app-20260904.log"), now),
            "保留期内的日志不得删除");

        // 当天日志 → 不应清理
        Assert.IsFalse(AppLog.IsExpiredLogFile(Path.Combine(_testDir, "app-20261003.log"), now));

        // 命名不符合约定（含用户可能手工放入的文件）→ 一律不动，避免激进删除
        Assert.IsFalse(AppLog.IsExpiredLogFile(Path.Combine(_testDir, "notes_backup.db"), now));
        Assert.IsFalse(AppLog.IsExpiredLogFile(Path.Combine(_testDir, "app-notadate.log"), now));
        Assert.IsFalse(AppLog.IsExpiredLogFile(Path.Combine(_testDir, "app-20260901.log.bak"), now));
    }

    /// <summary>
    /// F-P1-2 验证：导出/导入必须让「列表置顶」「桌面置顶」「窗口是否打开」三种状态独立往返，
    /// 不得因为 Note.IsPinned 兼容 setter 的「一写两改」把两种置顶语义强行合并。
    /// </summary>
    [TestMethod]
    public async Task F_P1_2_ExportImport_PreservesThreeIndependentStates()
    {
        var dbPath = Path.Combine(_testDir, "states.db");
        var dbContext = new SqliteDatabaseContext(dbPath);
        await dbContext.InitializeAndMigrateAsync();
        var repo = new NoteRepository(dbContext);
        var service = new ExportImportService(repo);

        // 三种组合各一条：仅列表置顶 / 仅桌面置顶 / 两者皆无但窗口开着
        var listPinnedOnly = new Note
        {
            Id = Guid.NewGuid(),
            Content = "仅列表置顶",
            IsPinnedInList = true,
            AlwaysOnTop = false,
            IsOpen = true
        };
        var desktopPinnedOnly = new Note
        {
            Id = Guid.NewGuid(),
            Content = "仅桌面置顶",
            IsPinnedInList = false,
            AlwaysOnTop = true,
            IsOpen = true
        };
        var plainOpen = new Note
        {
            Id = Guid.NewGuid(),
            Content = "无置顶但打开着",
            IsPinnedInList = false,
            AlwaysOnTop = false,
            IsOpen = true
        };

        await repo.SaveAsync(listPinnedOnly);
        await repo.SaveAsync(desktopPinnedOnly);
        await repo.SaveAsync(plainOpen);

        var exportFile = Path.Combine(_testDir, "states.json");
        Assert.AreEqual(3, await service.ExportNotesAsync(exportFile));

        // 还原到全新库
        var newDbContext = new SqliteDatabaseContext(Path.Combine(_testDir, "states_restored.db"));
        await newDbContext.InitializeAndMigrateAsync();
        var newRepo = new NoteRepository(newDbContext);
        var imported = await new ExportImportService(newRepo).ImportNotesAsync(exportFile);
        Assert.AreEqual(3, imported, "三条便签都应导入成功");

        var restored = await newRepo.GetAllAsync();
        var byId = restored.ToDictionary(n => n.Id);

        Assert.IsTrue(byId[listPinnedOnly.Id].IsPinnedInList, "「仅列表置顶」必须保持");
        Assert.IsFalse(byId[listPinnedOnly.Id].AlwaysOnTop, "「仅列表置顶」不得被兼容 setter 顺带打开桌面置顶");

        Assert.IsFalse(byId[desktopPinnedOnly.Id].IsPinnedInList, "「仅桌面置顶」不得被写成列表置顶");
        Assert.IsTrue(byId[desktopPinnedOnly.Id].AlwaysOnTop, "「仅桌面置顶」必须保持");

        Assert.IsTrue(byId[plainOpen.Id].IsOpen, "备份记录了 IsOpen=true 时必须还原为打开状态");
    }

    /// <summary>
    /// F-P1-2 验证：旧版（v1）备份里只有 IsPinned 字段时，导入应回落为「列表置顶」，
    /// 不得触发 Note.IsPinned 兼容 setter 把桌面置顶也一起打开；未记录 IsOpen 时保持既有行为（不还原窗口）。
    /// </summary>
    [TestMethod]
    public async Task F_P1_2_Import_LegacyBackupWithoutV2Fields_FallsBackSafely()
    {
        var legacyJson = """
            {
              "App": "StickyNotes",
              "Version": "1.0",
              "TotalCount": 1,
              "Notes": [
                {
                  "Id": "11111111-1111-1111-1111-111111111111",
                  "Content": "旧版备份便签",
                  "Color": "Blue",
                  "IsPinned": true,
                  "IsDeleted": false,
                  "WindowX": 200,
                  "WindowY": 220,
                  "WindowWidth": 380,
                  "WindowHeight": 420
                }
              ]
            }
            """;
        var legacyFile = Path.Combine(_testDir, "legacy.json");
        await File.WriteAllTextAsync(legacyFile, legacyJson);

        var dbContext = new SqliteDatabaseContext(Path.Combine(_testDir, "legacy.db"));
        await dbContext.InitializeAndMigrateAsync();
        var repo = new NoteRepository(dbContext);

        Assert.AreEqual(1, await new ExportImportService(repo).ImportNotesAsync(legacyFile));

        var note = (await repo.GetAllAsync()).Single();
        Assert.AreEqual("旧版备份便签", note.Content);
        Assert.IsTrue(note.IsPinnedInList, "旧备份的 IsPinned 应回落为列表置顶");
        Assert.IsFalse(note.AlwaysOnTop, "旧备份不得被兼容 setter 顺带打开桌面置顶");
        Assert.IsFalse(note.IsOpen, "旧备份未记录 IsOpen，应保持既有行为（不自动还原窗口）");
    }

    /// <summary>
    /// F-P1-2 验证：导入完成后必须以「全量重载」方式刷新主列表 —— 不得再借用
    /// NoteCreatedMessage + new Note() 的老写法（Note.Id 恒非 Guid.Empty，会凭空插入一张
    /// 空白且未持久化的幽灵便签）。
    /// </summary>
    [TestMethod]
    public void F_P1_2_Import_TriggersFullReload_InsteadOfPhantomNote()
    {
        TestEnvironment.RunInSta(() =>
        {
            var repo = new ReloadTrackingRepository(new Note
            {
                Id = Guid.NewGuid(),
                Content = "导入后应出现在列表里的便签",
                Color = NoteColor.Green
            });
            var wm = new WindowManager(TestEnvironment.CreateWindowManagerContainer(repo), repo);

            using var vm = new NotesListViewModel(repo, new SearchService(), wm);
            vm.LoadNotesAsync().GetAwaiter().GetResult();
            var loadsAfterInit = repo.GetAllActiveCallCount;

            // 用户执行「导入 JSON 备份」→ 服务层导入完成后发出重载请求
            WeakReferenceMessenger.Default.Send(new NotesReloadedRequestedMessage());
            PumpDispatcher(200);

            Assert.IsTrue(
                repo.GetAllActiveCallCount > loadsAfterInit,
                "收到 NotesReloadedRequestedMessage 后必须重新拉取全量便签");

            // 关键：列表中不得出现任何 Id == Guid.Empty 的幽灵卡片
            Assert.AreEqual(1, vm.Notes.Count, "导入后列表条目数应与数据库一致，不得凭空多出幽灵便签");
            Assert.IsFalse(vm.Notes.Any(n => n.Id == Guid.Empty), "不得插入未持久化的空白幽灵便签");
        });
    }

    private static void PumpDispatcher(int delayMilliseconds)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < delayMilliseconds)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                (Action)(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            Thread.Sleep(15);
        }
    }

    /// <summary>
    /// 记录 GetAllActiveAsync 调用次数，用于验证「导入 → 全量重载」链路真实发生。
    /// </summary>
    private sealed class ReloadTrackingRepository : INoteRepository
    {
        private readonly List<Note> _notes;
        public int GetAllActiveCallCount { get; private set; }

        public ReloadTrackingRepository(params Note[] notes) => _notes = notes.ToList();

        public Task<IReadOnlyList<Note>> GetAllActiveAsync(CancellationToken ct = default)
        {
            GetAllActiveCallCount++;
            return Task.FromResult<IReadOnlyList<Note>>(_notes.Where(n => !n.IsDeleted).ToList());
        }

        public Task<IReadOnlyList<Note>> GetAllArchivedAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Note>>(_notes.Where(n => n.IsDeleted).ToList());

        public Task<IReadOnlyList<Note>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Note>>(_notes.ToList());

        public Task<Note?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(_notes.FirstOrDefault(n => n.Id == id));

        public Task<ApplyRemoteResult> ApplyRemoteBatchAsync(IEnumerable<RemoteApplyItem> items, CancellationToken ct = default) =>
            Task.FromResult(new ApplyRemoteResult(0, 0));

        public Task SaveAsync(Note note, CancellationToken ct = default)
        {
            var idx = _notes.FindIndex(n => n.Id == note.Id);
            if (idx >= 0) _notes[idx] = note;
            else _notes.Add(note);
            return Task.CompletedTask;
        }

        public Task SaveBatchAsync(IEnumerable<Note> notes, CancellationToken ct = default) =>
            Task.WhenAll(notes.Select(n => SaveAsync(n, ct)));

        public Task SoftDeleteAsync(Guid id, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ArchiveNoteAsync(Guid id, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task RestoreNoteAsync(Guid id, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task HardDeleteAsync(Guid id, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ClearAllArchivedAsync(CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UpdateWindowBoundsAsync(Guid id, double x, double y, double width, double height, bool isOpen, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UpdateWindowPlacementAsync(Guid id, double x, double y, double width, double height, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
