using System;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.Messaging;
using StickyNotes.Infrastructure;
using StickyNotes.Messages;
using StickyNotes.Services.Localization;

namespace StickyNotes.Services;

/// <summary>
/// 系统全局快捷键服务 (Win+Alt+N, Win+Alt+H)
/// </summary>
public sealed class HotKeyService : IDisposable
{
    private const int HotKeyId_NewNote = 9001;
    private const int HotKeyId_ShowList = 9002;

    private readonly SettingsService _settingsService;
    private HwndSource? _hwndSource;

    /// <summary>当前注册成功的热键 ID 集合（按键级跟踪，支持失败键的后续重试）</summary>
    private readonly HashSet<int> _registeredIds = new();

    /// <summary>是否至少有一个全局热键处于注册成功状态</summary>
    public bool IsRegistered => _registeredIds.Count > 0;

    public HotKeyService(SettingsService settingsService)
    {
        _settingsService = settingsService;

        // 监听设置变更
        WeakReferenceMessenger.Default.Register<HotKeyConfigChangedMessage>(this, (_, msg) =>
        {
            if (msg.Value)
            {
                RegisterHotKeys();
            }
            else
            {
                UnregisterHotKeys();
            }
        });
    }

    /// <summary>
    /// 初始化全局消息接收窗口并按需注册快捷键
    /// </summary>
    public void Initialize()
    {
        if (_hwndSource != null) return;

        var parameters = new HwndSourceParameters("StickyNotes_HotKeyHelper")
        {
            WindowStyle = 0,
            Width = 0,
            Height = 0
        };

        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(HwndHook);

        if (_settingsService.EnableGlobalHotKeys)
        {
            RegisterHotKeys();
        }
    }

    public void RegisterHotKeys()
    {
        if (_hwndSource == null) return;

        IntPtr handle = _hwndSource.Handle;
        uint modifiers = NativeMethods.MOD_WIN | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT;

        // 按键级注册：已成功的键跳过（重复 RegisterHotKey 会失败并被误报为「被占用」），
        // 失败的键在下次开关热键或重进设置时自动重试。
        // 原实现用单个 _isRegistered 布尔：部分成功后再调用本方法会整体短路，
        // 失败的那一个键永远没有重试机会（N-11）。
        TryRegisterHotKey(handle, HotKeyId_NewNote, modifiers, NativeMethods.VK_N, "Win+Alt+N",
            Loc.T("HotKey.NewNote", "快速新建"));
        TryRegisterHotKey(handle, HotKeyId_ShowList, modifiers, NativeMethods.VK_H, "Win+Alt+H",
            Loc.T("HotKey.ShowList", "呼出列表"));

        AppLog.Info($"[HotKeyService] 全局热键注册结果: 已成功 {_registeredIds.Count}/2");
    }

    private void TryRegisterHotKey(IntPtr handle, int id, uint modifiers, uint vk, string name, string purpose)
    {
        if (_registeredIds.Contains(id)) return;

        if (NativeMethods.RegisterHotKey(handle, id, modifiers, vk))
        {
            _registeredIds.Add(id);
            AppLog.Info($"[HotKeyService] 全局热键 {name} 注册成功 ({purpose})");
        }
        else
        {
            AppLog.Warn($"[HotKeyService] 全局热键 {name} 注册失败 (可能被其它软件占用)");
        }
    }

    public void UnregisterHotKeys()
    {
        if (_hwndSource == null || _registeredIds.Count == 0) return;

        IntPtr handle = _hwndSource.Handle;
        foreach (var id in _registeredIds)
        {
            NativeMethods.UnregisterHotKey(handle, id);
        }
        _registeredIds.Clear();
        AppLog.Info("[HotKeyService] 全局热键已注销");
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            int hotkeyId = wParam.ToInt32();
            if (hotkeyId == HotKeyId_NewNote)
            {
                AppLog.Info("[HotKeyService] 捕获全局热键 Win+Alt+N，发出新建请求");
                WeakReferenceMessenger.Default.Send(new NewNoteRequestedMessage());
                handled = true;
            }
            else if (hotkeyId == HotKeyId_ShowList)
            {
                AppLog.Info("[HotKeyService] 捕获全局热键 Win+Alt+H，发出唤醒主列表请求");
                WeakReferenceMessenger.Default.Send(new ShowNotesListRequestedMessage());
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
        UnregisterHotKeys();
        _hwndSource?.RemoveHook(HwndHook);
        _hwndSource?.Dispose();
        _hwndSource = null;
    }
}
