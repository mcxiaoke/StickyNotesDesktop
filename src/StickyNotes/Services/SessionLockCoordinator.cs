using System.Windows.Input;
using Microsoft.Win32;
using System.Windows.Threading;
using StickyNotes.Infrastructure;

namespace StickyNotes.Services;

/// <summary>会话锁定的原因，用于展示差异化的提示文案</summary>
public enum PinLockReason
{
    /// <summary>会话开始（冷启动）时即锁定</summary>
    SessionStart,

    /// <summary>闲置超过设定阈值后自动锁定</summary>
    IdleTimeout,

    /// <summary>检测到系统锁屏而强制锁定</summary>
    ScreenLock
}

/// <summary>
/// 会话级空闲锁：整个应用进程内**唯一**的锁定状态源。
///
/// 设计要点（详见 docs/PIN-IDLE-LOCK-DESIGN-20261011.md）：
/// <list type="bullet">
///   <item>
///     **闲置 = 无任何键鼠输入的时长**，与窗口是否可见/是否在前台无关。
///     旧实现用 <c>Window.Deactivated</c> 起算计时，人离开但窗口仍停在前台时永远不会上锁，
///     恰恰漏掉了防偷窥最需要覆盖的场景。
///   </item>
///   <item>
///     **吸收态**：一旦 <see cref="IsLocked"/> 为 true，<see cref="NotifyUserActivity"/> 只刷新时间戳
///     而不解除锁定 —— 否则用户点一下遮罩就会白锁。唯一解锁入口是 <see cref="Unlock"/>，
///     只由 PIN 验证成功后调用。
///   </item>
///   <item>
///     计时器是**进程级常驻**的，不依附于任何窗口，因此窗口隐藏到托盘期间仍在计时。
///   </item>
/// </list>
///
/// 本类不持有任何 UI 元素，只负责状态与判定；呈现完全交给各窗口的 PinLockOverlay。
/// </summary>
public sealed class SessionLockCoordinator : IDisposable
{
    /// <summary>判定轮询间隔。仅比对时间戳，不做任何 UI 工作，开销可忽略。</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly SettingsService? _settingsService;
    private readonly PinService? _pinService;
    private readonly Func<DateTime> _utcNow;

    /// <summary>测试模式下为 null：不创建、不启动，避免依赖真实调度器与全局输入钩子。</summary>
    private readonly DispatcherTimer? _timer;

    /// <summary>是否真的挂过全局钩子。Dispose 必须据此判断，否则会凭空构造一个从未用过的 InputManager。</summary>
    private readonly bool _runtimeHooksAttached;

    private bool _disposed;

    /// <summary>是否已锁定（含冷启动初始锁定）。窗口侧还需与 <see cref="PinService.IsPinEnabled"/> 取交集。</summary>
    public bool IsLocked { get; private set; }

    /// <summary>最近一次进入锁定态的原因，供遮罩展示差异化提示。</summary>
    public PinLockReason LockReason { get; private set; } = PinLockReason.SessionStart;

    /// <summary>最近一次键鼠输入时刻（UTC）。初始值为构造时刻，即冷启动后闲置计时从此刻开始。</summary>
    public DateTime LastActivityUtc { get; private set; }

    /// <summary>进入锁定态时触发（含冷启动初始锁定）。各窗口据此覆盖 PIN 遮罩。</summary>
    public event EventHandler? LockRequested;

    /// <summary>PIN 验证成功后退出锁定态时触发。各窗口据此收起遮罩。</summary>
    public event EventHandler? Unlocked;

    /// <summary>生产构造（由 DI 调用）。</summary>
    public SessionLockCoordinator(SettingsService? settingsService = null, PinService? pinService = null)
        : this(settingsService, pinService, () => DateTime.UtcNow, attachRuntimeHooks: true)
    {
    }

    /// <summary>
    /// 测试构造：注入假时钟并可关闭全局钩子，使超时判定无需真实等待。
    /// 该构造为 internal —— DI 只认 public 构造，不会误选。
    /// </summary>
    internal SessionLockCoordinator(
        SettingsService? settingsService,
        PinService? pinService,
        Func<DateTime> utcNow,
        bool attachRuntimeHooks)
    {
        _settingsService = settingsService;
        _pinService = pinService;
        _utcNow = utcNow;
        LastActivityUtc = utcNow();

        // 冷启动即上锁：PIN 启用且未显式关闭该开关时，进程一启动就是锁定态。
        // 这是防「托盘退出 → 重新双击 exe 绕过锁定」的唯一防线。
        IsLocked = (_pinService?.IsPinEnabled ?? false)
                  && (_settingsService?.LockOnLaunch ?? true);
        LockReason = PinLockReason.SessionStart;

        if (!attachRuntimeHooks)
        {
            return;
        }

        InputManager.Current.PreProcessInput += OnPreProcessInput;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _runtimeHooksAttached = true;

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TickInterval };
        _timer.Tick += OnTick;
        _timer.Start();

        AppLog.Info(
            $"[SessionLockCoordinator] 会话锁已启动，初始锁定={IsLocked}，" +
            $"闲置阈值={_settingsService?.IdleLockMinutes ?? 0} 分钟，冷启动锁定={_settingsService?.LockOnLaunch ?? true}");
    }

    /// <summary>
    /// 用户输入回调（键盘 / 鼠标 / 触摸笔，全局）。
    /// 锁定态下同样刷新时间戳：解锁后立即重新开始计时，避免「解锁瞬间就再次超时」。
    /// </summary>
    private void OnPreProcessInput(object sender, PreProcessInputEventArgs e) => NotifyUserActivity();

    private void OnTick(object? sender, EventArgs e) => Evaluate();

    /// <summary>
    /// 锁屏时立即强制上锁。人离开工位的第一时间就锁住，比等待闲置阈值更快也更可靠
    /// （锁屏期间没有任何输入事件，阈值再短也感知不到）。
    /// </summary>
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason != SessionSwitchReason.SessionLock) return;
        if (_pinService?.IsPinEnabled != true) return;

        AppLog.Info("[SessionLockCoordinator] 检测到会话锁定，强制上锁");
        ForceLock(PinLockReason.ScreenLock);
    }

    /// <summary>记录一次用户活动（仅刷新时间戳，**不**解除锁定）。</summary>
    public void NotifyUserActivity() => LastActivityUtc = _utcNow();

    /// <summary>退出锁定态。仅由 PIN 验证成功后调用，同时把闲置计时归零。</summary>
    public void Unlock()
    {
        LastActivityUtc = _utcNow();
        if (!IsLocked) return;

        IsLocked = false;
        AppLog.Info("[SessionLockCoordinator] 会话已解锁");
        Unlocked?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>无条件进入锁定态（锁屏、启动初值等场景）。重复调用不重复触发事件。</summary>
    public void ForceLock(PinLockReason reason = PinLockReason.IdleTimeout)
    {
        if (IsLocked) return;

        IsLocked = true;
        LockReason = reason;
        LockRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 轮询判定：闲置时长达到阈值则上锁。由计时器驱动；测试中可直接调用以配合假时钟。
    /// </summary>
    internal void Evaluate()
    {
        if (IsLocked || _disposed) return;

        var idleMinutes = _settingsService?.IdleLockMinutes ?? 0;
        if (idleMinutes <= 0) return;

        if (!ShouldLock(_utcNow() - LastActivityUtc, idleMinutes)) return;

        AppLog.Info($"[SessionLockCoordinator] 已闲置 {idleMinutes} 分钟，会话自动锁定");
        ForceLock(PinLockReason.IdleTimeout);
    }

    /// <summary>
    /// 纯判定函数：闲置时长是否达到阈值。抽成静态以便单测直接覆盖边界，无需等待真实时间。
    /// </summary>
    internal static bool ShouldLock(TimeSpan idle, int idleLockMinutes) =>
        idleLockMinutes > 0 && idle >= TimeSpan.FromMinutes(idleLockMinutes);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_timer != null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
        }

        if (!_runtimeHooksAttached) return;

        InputManager.Current.PreProcessInput -= OnPreProcessInput;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
    }
}
