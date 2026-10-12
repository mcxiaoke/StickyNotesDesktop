using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StickyNotes.Services;

namespace StickyNotes.Tests;

/// <summary>
/// 会话级空闲锁单元测试（详见 docs/PIN-IDLE-LOCK-DESIGN-20261011.md）。
///
/// 全部通过 internal 构造注入<b>假时钟</b>，既不依赖真实等待，也不挂全局输入钩子与调度器计时，
/// 因此全部用例可在毫秒级跑完且完全确定。
/// </summary>
[TestClass]
public class SessionLockCoordinatorTests
{
    private string _testDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"StickyNotes_SessionLock_{Guid.NewGuid():N}");
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

    /// <summary>可手动推进的假时钟</summary>
    private sealed class FakeClock
    {
        public DateTime Now { get; private set; } = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
        public DateTime UtcNow() => Now;
        public void Advance(TimeSpan delta) => Now += delta;
    }

    private SettingsService NewSettings(string fileName = "s.json") =>
        new(Path.Combine(_testDir, fileName));

    /// <summary>构造被测对象；<paramref name="enablePin"/> 决定是否已设置有效 PIN。</summary>
    private (SessionLockCoordinator Lock, FakeClock Clock, SettingsService Settings) Create(
        bool enablePin,
        bool lockOnLaunch = true,
        int idleLockMinutes = 10,
        string fileName = "s.json")
    {
        var settings = NewSettings(fileName);
        settings.Settings.IdleLockMinutes = idleLockMinutes;
        settings.Settings.LockOnLaunch = lockOnLaunch;

        var pin = new PinService(settings);
        if (enablePin)
        {
            pin.SetPin("test-pin-1234");
        }

        var clock = new FakeClock();
        var coordinator = new SessionLockCoordinator(settings, pin, clock.UtcNow, attachRuntimeHooks: false);
        return (coordinator, clock, settings);
    }

    // ---------------- 纯判定函数边界 ----------------

    [TestMethod]
    public void ShouldLock_未达阈值_不锁()
    {
        Assert.IsFalse(SessionLockCoordinator.ShouldLock(TimeSpan.FromMinutes(9.99), 10));
    }

    [TestMethod]
    public void ShouldLock_恰好达到阈值_锁()
    {
        Assert.IsTrue(SessionLockCoordinator.ShouldLock(TimeSpan.FromMinutes(10), 10));
    }

    [TestMethod]
    public void ShouldLock_阈值为零_永不锁()
    {
        // 0 = 不启用：即使闲置一整天也不得触发锁定
        Assert.IsFalse(SessionLockCoordinator.ShouldLock(TimeSpan.FromDays(7), 0));
        Assert.IsFalse(SessionLockCoordinator.ShouldLock(TimeSpan.Zero, 0));
    }

    // ---------------- 冷启动初始状态 ----------------

    [TestMethod]
    public void 冷启动且启用PIN_初始即为锁定()
    {
        var (lock_, _, _) = Create(enablePin: true);

        Assert.IsTrue(lock_.IsLocked, "托盘退出后重开 exe 必须仍然要求 PIN，否则锁定形同虚设");
        Assert.AreEqual(PinLockReason.SessionStart, lock_.LockReason);
    }

    [TestMethod]
    public void 冷启动但显式关闭启动锁定_初始不锁()
    {
        var (lock_, _, _) = Create(enablePin: true, lockOnLaunch: false);

        Assert.IsFalse(lock_.IsLocked, "用户显式关闭该开关时不得强制冷启动锁定");
    }

    [TestMethod]
    public void 未启用PIN_初始不锁()
    {
        var (lock_, _, _) = Create(enablePin: false);

        Assert.IsFalse(lock_.IsLocked, "没有有效 PIN 时不得锁定，否则用户会被关在门外");
    }

    // ---------------- 闲置超时锁定 ----------------

    [TestMethod]
    public void 闲置超时后Evaluate_触发锁定且事件只发一次()
    {
        var (lock_, clock, settings) = Create(enablePin: true, lockOnLaunch: false, idleLockMinutes: 5);

        int lockEvents = 0;
        lock_.LockRequested += (_, _) => lockEvents++;

        clock.Advance(TimeSpan.FromMinutes(4));
        lock_.Evaluate();
        Assert.IsFalse(lock_.IsLocked, "未到阈值不得锁定");
        Assert.AreEqual(0, lockEvents);

        clock.Advance(TimeSpan.FromMinutes(1));
        lock_.Evaluate();
        Assert.IsTrue(lock_.IsLocked, "达到阈值必须锁定 —— 这是「人离开但窗口在前台」场景的核心保障");
        Assert.AreEqual(1, lockEvents);
        Assert.AreEqual(PinLockReason.IdleTimeout, lock_.LockReason);

        // 继续空转不应重复触发事件
        clock.Advance(TimeSpan.FromMinutes(30));
        lock_.Evaluate();
        Assert.AreEqual(1, lockEvents, "已锁定后重复判定不得重复触发锁定事件");

        Assert.AreEqual(5, settings.IdleLockMinutes, "设置项本身不应被判定逻辑改动");
    }

    [TestMethod]
    public void 闲置阈值设为零_永不自动锁定()
    {
        var (lock_, clock, _) = Create(enablePin: true, lockOnLaunch: false, idleLockMinutes: 0);

        clock.Advance(TimeSpan.FromHours(8));
        lock_.Evaluate();

        Assert.IsFalse(lock_.IsLocked, "用户选择「不启用」时不得自动锁定");
    }

    [TestMethod]
    public void 用户活动刷新时间戳_推迟锁定()
    {
        var (lock_, clock, _) = Create(enablePin: true, lockOnLaunch: false, idleLockMinutes: 5);

        clock.Advance(TimeSpan.FromMinutes(4));
        lock_.NotifyUserActivity();

        clock.Advance(TimeSpan.FromMinutes(4));
        lock_.Evaluate();
        Assert.IsFalse(lock_.IsLocked, "中途有输入应重新计时");

        clock.Advance(TimeSpan.FromMinutes(1));
        lock_.Evaluate();
        Assert.IsTrue(lock_.IsLocked);
    }

    // ---------------- 吸收态：锁定后活动不得自动解锁 ----------------

    [TestMethod]
    public void 已锁定后用户活动_不得自动解锁()
    {
        var (lock_, clock, _) = Create(enablePin: true, lockOnLaunch: false, idleLockMinutes: 5);

        clock.Advance(TimeSpan.FromMinutes(6));
        lock_.Evaluate();
        Assert.IsTrue(lock_.IsLocked);

        // 用户在遮罩上敲键盘 / 点鼠标
        lock_.NotifyUserActivity();
        clock.Advance(TimeSpan.FromSeconds(1));
        lock_.Evaluate();

        Assert.IsTrue(lock_.IsLocked, "锁定必须是吸收态：点一下遮罩不能白锁");
    }

    [TestMethod]
    public void Unlock_退出锁定并把闲置计时归零()
    {
        var (lock_, clock, _) = Create(enablePin: true, lockOnLaunch: false, idleLockMinutes: 5);

        clock.Advance(TimeSpan.FromMinutes(6));
        lock_.Evaluate();

        int unlockEvents = 0;
        lock_.Unlocked += (_, _) => unlockEvents++;

        clock.Advance(TimeSpan.FromMinutes(3));
        lock_.Unlock();

        Assert.IsFalse(lock_.IsLocked);
        Assert.AreEqual(1, unlockEvents);
        Assert.AreEqual(clock.Now, lock_.LastActivityUtc, "解锁必须重置闲置起点，避免解锁瞬间再次超时");

        clock.Advance(TimeSpan.FromMinutes(4));
        lock_.Evaluate();
        Assert.IsFalse(lock_.IsLocked, "解锁后应重新按闲置时长计时");
    }

    [TestMethod]
    public void Unlock_在未锁定态下调用_不得触发事件()
    {
        var (lock_, _, _) = Create(enablePin: true, lockOnLaunch: false);

        int unlockEvents = 0;
        lock_.Unlocked += (_, _) => unlockEvents++;

        lock_.Unlock();

        Assert.AreEqual(0, unlockEvents, "未锁定时解锁属于空操作，不得驱动 UI 收起遮罩");
        Assert.IsFalse(lock_.IsLocked);
    }

    // ---------------- ForceLock ----------------

    [TestMethod]
    public void ForceLock_幂等且记录原因()
    {
        var (lock_, _, _) = Create(enablePin: true, lockOnLaunch: false);

        int lockEvents = 0;
        lock_.LockRequested += (_, _) => lockEvents++;

        lock_.ForceLock(PinLockReason.ScreenLock);
        lock_.ForceLock(PinLockReason.IdleTimeout);

        Assert.IsTrue(lock_.IsLocked);
        Assert.AreEqual(1, lockEvents, "重复锁定不得重复驱动 UI");
        Assert.AreEqual(PinLockReason.ScreenLock, lock_.LockReason, "首次锁定原因不应被后续调用覆盖");
    }

    // ---------------- 设置落库 ----------------

    [TestMethod]
    public void IdleLockMinutes_超出范围被夹紧并落库()
    {
        var settings = NewSettings("clamp.json");

        settings.IdleLockMinutes = 9999;
        Assert.AreEqual(120, settings.IdleLockMinutes);

        settings.IdleLockMinutes = -5;
        Assert.AreEqual(0, settings.IdleLockMinutes);

        var reloaded = new SettingsService(Path.Combine(_testDir, "clamp.json"));
        Assert.AreEqual(0, reloaded.IdleLockMinutes, "闲置锁定时长必须持久化");
    }

    [TestMethod]
    public void LockOnLaunch_默认值与持久化()
    {
        var settings = NewSettings("launch.json");
        Assert.IsTrue(settings.LockOnLaunch, "冷启动锁定默认开启");

        settings.LockOnLaunch = false;
        var reloaded = new SettingsService(Path.Combine(_testDir, "launch.json"));
        Assert.IsFalse(reloaded.LockOnLaunch, "冷启动锁定开关必须持久化");
    }

    /// <summary>
    /// DI 装配回归：本类存在一个 internal 的 4 参测试构造，若 DI 误选它或无法解析可选项，
    /// 会在应用启动时才炸（表现为闪退）。此处按生产注册方式解析一次，确保注入到位。
    /// </summary>
    [TestMethod]
    public void DI容器解析_必须命中生产构造并注入依赖()
    {
        var settings = NewSettings("di.json");
        var pin = new PinService(settings);
        pin.SetPin("test-pin-1234");

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton(settings);
        services.AddSingleton(pin);
        services.AddSingleton<SessionLockCoordinator>();

        TestEnvironment.RunInSta(() =>
        {
            using var provider = services.BuildServiceProvider();
            var coordinator = provider.GetRequiredService<SessionLockCoordinator>();

            Assert.IsNotNull(coordinator, "DI 必须能解析会话锁");
            Assert.IsTrue(coordinator.IsLocked, "冷启动且启用 PIN 时应解析即为锁定态");
        });
    }
}
