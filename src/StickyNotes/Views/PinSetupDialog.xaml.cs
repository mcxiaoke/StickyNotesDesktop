using System.Windows;
using StickyNotes.Services;
using StickyNotes.Services.Localization;

namespace StickyNotes.Views;

/// <summary>对话框模式：启用并设置新 PIN / 修改 PIN / 清除 PIN</summary>
public enum PinDialogMode
{
    Enable,
    Change,
    Disable
}

/// <summary>对话框结果：Confirmed 为 true 时各字段有效</summary>
public record PinDialogResult(bool Confirmed, string CurrentPin, string NewPin);

/// <summary>
/// PIN 设置对话框：根据模式动态展示「当前 PIN / 新 PIN / 确认新 PIN」输入项。
/// 服务调用（SetPin/ClearPin）由调用方在确认后执行，本对话框只负责收集与校验输入。
/// </summary>
public partial class PinSetupDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly PinDialogMode _mode;
    private readonly PinService _pinService;

    public PinSetupDialog(PinDialogMode mode, PinService pinService)
    {
        InitializeComponent();
        _mode = mode;
        _pinService = pinService;

        ApplyMode();
        Loaded += (_, _) =>
        {
            var first = _mode == PinDialogMode.Enable ? (System.Windows.Controls.PasswordBox)NewPinBox : CurrentPinBox;
            first.Focus();
        };
    }

    private void ApplyMode()
    {
        switch (_mode)
        {
            case PinDialogMode.Enable:
                HeaderText.Text = Loc.T("Pin.Setup.SetHeader", "设置 PIN");
                SubHeaderText.Text = Loc.T("Pin.Setup.SetSubHeader",
                    "设置后打开便签列表与归档时需输入 PIN（4~20 位，内容不限，轻量防偷窥）");
                CurrentPinLabel.Visibility = Visibility.Collapsed;
                CurrentPinBox.Visibility = Visibility.Collapsed;
                break;

            case PinDialogMode.Change:
                HeaderText.Text = Loc.T("Pin.Setup.ChangeHeader", "修改 PIN");
                SubHeaderText.Text = Loc.T("Pin.Setup.ChangeSubHeader",
                    "需先验证当前 PIN，再输入新 PIN（4~20 位，内容不限）");
                break;

            case PinDialogMode.Disable:
                HeaderText.Text = Loc.T("Pin.Setup.ClearHeader", "清除 PIN");
                SubHeaderText.Text = Loc.T("Pin.Setup.ClearSubHeader",
                    "输入当前 PIN 验证通过后即可关闭 PIN 锁定");
                NewPinLabel.Visibility = Visibility.Collapsed;
                NewPinBox.Visibility = Visibility.Collapsed;
                ConfirmPinLabel.Visibility = Visibility.Collapsed;
                ConfirmPinBox.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        string current = CurrentPinBox.Password;
        string newPin = NewPinBox.Password;
        string confirm = ConfirmPinBox.Password;

        if (_mode != PinDialogMode.Enable)
        {
            if (!_pinService.VerifyPin(current))
            {
                ShowError(Loc.T("Pin.Error.Incorrect", "当前 PIN 不正确"));
                return;
            }
        }

        if (_mode != PinDialogMode.Disable)
        {
            if (!PinService.IsValidPin(newPin))
            {
                ShowError(Loc.T("Pin.Error.Length", "新 PIN 长度须为 {0}~{1} 位",
                    PinService.MinPinLength, PinService.MaxPinLength));
                return;
            }

            if (newPin != confirm)
            {
                ShowError(Loc.T("Pin.Error.Mismatch", "两次输入的新 PIN 不一致"));
                return;
            }
        }

        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 收集输入并执行对应服务调用：Enable → SetPin，Change → SetPin，Disable → ClearPin
    /// </summary>
    public static bool Execute(Window? owner, PinService pinService, PinDialogMode mode)
    {
        var dialog = new PinSetupDialog(mode, pinService);
        if (owner != null) dialog.Owner = owner;

        if (dialog.ShowDialog() != true) return false;

        return mode switch
        {
            PinDialogMode.Enable => pinService.SetPin(dialog.NewPinBox.Password),
            PinDialogMode.Change => pinService.SetPin(dialog.NewPinBox.Password),
            PinDialogMode.Disable => pinService.ClearPin(dialog.CurrentPinBox.Password),
            _ => false
        };
    }
}
