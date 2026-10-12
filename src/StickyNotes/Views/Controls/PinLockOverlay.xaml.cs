using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using StickyNotes.Messages;
using StickyNotes.Services;
using StickyNotes.Services.Localization;

namespace StickyNotes.Views.Controls;

/// <summary>
/// PIN 锁定遮罩：以完全不透明层盖住宿主窗口内容区，输对 PIN 后通过 Unlocked 事件放行。
/// 宿主只与 IsLocked 属性和 Unlocked 事件交互，验证逻辑全部委托给 PinService。
/// </summary>
public partial class PinLockOverlay : UserControl
{
    /// <summary>宿主窗口注入的 PIN 服务（为空时视为未启用锁定）</summary>
    public PinService? PinService { get; set; }

    /// <summary>解锁成功后触发（宿主无需再做其它事，遮罩自行隐藏）</summary>
    public event EventHandler? Unlocked;

    /// <summary>
    /// 遮罩提示文案。区分「冷启动即锁」与「闲置自动锁定」，
    /// 让用户知道是自己设的阈值生效了，而不是误以为程序出了故障。
    /// </summary>
    public static readonly DependencyProperty LockReasonProperty =
        DependencyProperty.Register(
            nameof(LockReason),
            typeof(PinLockReason),
            typeof(PinLockOverlay),
            new PropertyMetadata(PinLockReason.SessionStart, OnLockReasonChanged));

    public PinLockReason LockReason
    {
        get => (PinLockReason)GetValue(LockReasonProperty);
        set => SetValue(LockReasonProperty, value);
    }

    private static void OnLockReasonChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PinLockOverlay overlay) return;
        overlay.HintText.Text = e.NewValue is PinLockReason.IdleTimeout
            ? Loc.T("Pin.HintAutoLocked", "长时间未操作，已自动锁定")
            : Loc.T("Pin.HintDefault", "输入 PIN 后按 Enter 解锁");
    }

    public PinLockOverlay()
    {
        InitializeComponent();

        // HintText 在 XAML 里已走 {loc:Loc ...} 绑定会随语言自动刷新，
        // 但 OnLockReasonChanged 是代码赋值，会覆盖绑定值，故这里只做一次性订阅，
        // 让锁定原因在切换语言后仍能重新计算。
        WeakReferenceMessenger.Default.Register<LanguageChangedMessage>(this, (_, _) =>
            HintText.Text = LockReason is PinLockReason.IdleTimeout
                ? Loc.T("Pin.HintAutoLocked", "长时间未操作，已自动锁定")
                : Loc.T("Pin.HintDefault", "输入 PIN 后按 Enter 解锁"));
    }

    public static readonly DependencyProperty IsLockedProperty =
        DependencyProperty.Register(
            nameof(IsLocked),
            typeof(bool),
            typeof(PinLockOverlay),
            new PropertyMetadata(false, OnIsLockedChanged));

    /// <summary>锁定状态：true 显示遮罩并聚焦输入框，false 隐藏</summary>
    public bool IsLocked
    {
        get => (bool)GetValue(IsLockedProperty);
        set => SetValue(IsLockedProperty, value);
    }

    private static void OnIsLockedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PinLockOverlay overlay) return;

        bool locked = (bool)e.NewValue;
        overlay.RootGrid.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;

        if (locked)
        {
            overlay.PinBox.Clear();
            overlay.ErrorText.Visibility = Visibility.Collapsed;
            // 延迟到布局完成后再聚焦，避免焦点被宿主窗口抢走
            overlay.Dispatcher.BeginInvoke(() => overlay.PinBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void PinBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            TryUnlock();
            e.Handled = true;
        }
    }

    private void TryUnlock()
    {
        // 宿主未注入服务或 PIN 已在设置中被移除时，直接放行（避免把自己锁死在外面）
        if (PinService == null || !PinService.IsPinEnabled)
        {
            Unlock();
            return;
        }

        if (PinService.VerifyPin(PinBox.Password))
        {
            Unlock();
        }
        else
        {
            ErrorText.Visibility = Visibility.Visible;
            PinBox.Clear();
            PinBox.Focus();
            ShakeInput();
        }
    }

    private void Unlock()
    {
        IsLocked = false;
        Unlocked?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 测试钩子：以「验证通过」的完整路径解锁，确保宿主注册的 <see cref="Unlocked"/> 回调被真实触发。
    /// 绝不能只改 <see cref="IsLocked"/> —— 那会跳过宿主与会话锁之间的联动，测的就不是生产链路。
    /// </summary>
    internal void UnlockForTest() => Unlock();

    private void ShakeInput()
    {
        var translate = new TranslateTransform();
        InputPanel.RenderTransform = translate;

        var animation = new DoubleAnimationUsingKeyFrames
        {
            KeyFrames =
            {
                new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                new EasingDoubleKeyFrame(-8, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(60))),
                new EasingDoubleKeyFrame(8, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))),
                new EasingDoubleKeyFrame(-4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180))),
                new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(240)))
            }
        };
        translate.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    private void ForgotPin_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            Loc.T("Pin.ForgotBody",
                "PIN 为轻量防偷窥保护，不提供应用内找回。\n\n" +
                "如需重置：先退出本应用，再打开数据目录下的 settings.json，\n" +
                "删除 \"PinEnabled\"、\"PinSalt\"、\"PinHash\" 三个字段后保存，重新启动即可。"),
            Loc.T("Pin.ForgotTitle", "忘记 PIN"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
