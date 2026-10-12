using CommunityToolkit.Mvvm.Messaging.Messages;
using StickyNotes.Models;

namespace StickyNotes.Messages;

/// <summary>
/// 便签正文内容变更消息（轻量，仅用于卡片文本和搜索快照更新，不触发列表卡片位置重排）
/// </summary>
public sealed class NoteContentChangedMessage : ValueChangedMessage<(Guid NoteId, string Content, DateTime UpdatedAt)>
{
    public NoteContentChangedMessage(Guid noteId, string content, DateTime updatedAt)
        : base((noteId, content, updatedAt)) { }
}

/// <summary>
/// 便签元数据变更消息（置顶、颜色、软删除等，需要重新排定列表次序或刷新样式）
/// </summary>
public sealed class NoteMetaChangedMessage : ValueChangedMessage<Note>
{
    public NoteMetaChangedMessage(Note value) : base(value) { }
}

/// <summary>
/// 便签通用更新消息（保持向下兼容）
/// </summary>
public sealed class NoteUpdatedMessage : ValueChangedMessage<Note>
{
    public NoteUpdatedMessage(Note value) : base(value) { }
}

/// <summary>
/// 便签删除消息
/// </summary>
public sealed class NoteDeletedMessage : ValueChangedMessage<Guid>
{
    public NoteDeletedMessage(Guid noteId) : base(noteId) { }
}

/// <summary>
/// 新便签创建消息
/// </summary>
public sealed class NoteCreatedMessage : ValueChangedMessage<Note>
{
    public NoteCreatedMessage(Note value) : base(value) { }
}

/// <summary>
/// 便签已归档消息
/// </summary>
public sealed class NoteArchivedMessage : ValueChangedMessage<Guid>
{
    public NoteArchivedMessage(Guid noteId) : base(noteId) { }
}

/// <summary>
/// 便签已从归档恢复消息
/// </summary>
public sealed class NoteRestoredMessage : ValueChangedMessage<Note>
{
    public NoteRestoredMessage(Note value) : base(value) { }
}

/// <summary>
/// 请求主列表全量重载便签消息（用于导入备份、批量外部变更等无法逐条反映的场景）。
/// </summary>
/// <remarks>
/// 导入备份后**必须**用本消息触发 <c>LoadNotesAsync</c> 全量重载，不得再借用
/// <see cref="NoteCreatedMessage"/> + <c>new Note()</c> 的老写法：<see cref="Note.Id"/>
/// 的属性初始化器是 <c>Guid.NewGuid()</c>，"Id 恒非 Guid.Empty"，那条路径会走"插入单张卡片"
/// 分支，凭空插入一张空白且未持久化的幽灵便签，而真实导入的便签要等重启才可见（原 F-P1-2 子问题 2）。
/// </remarks>
public sealed class NotesReloadedRequestedMessage { }

/// <summary>
/// 便签编辑器字体大小变更消息
/// </summary>
public sealed class FontSizeChangedMessage : ValueChangedMessage<double>
{
    public FontSizeChangedMessage(double newFontSize) : base(newFontSize) { }
}

/// <summary>
/// 请求新建便签消息（跨窗口解耦）
/// </summary>
public sealed class NewNoteRequestedMessage { }

/// <summary>
/// 请求唤醒并呈现便签主列表窗口消息（跨窗口解耦）
/// </summary>
public sealed class ShowNotesListRequestedMessage { }

/// <summary>
/// 全局快捷键启用状态变更消息
/// </summary>
public sealed class HotKeyConfigChangedMessage : ValueChangedMessage<bool>
{
    public HotKeyConfigChangedMessage(bool enabled) : base(enabled) { }
}

/// <summary>
/// 界面语言切换消息（值 = 生效的语言代码）
/// </summary>
/// <remarks>
/// 用弱引用总线而非裸事件，与本项目既有解耦方式保持一致；
/// 订阅方需在窗口关闭时自行退订（弱引用总线不会阻止回收，但仍应保持习惯）。
/// </remarks>
public sealed class LanguageChangedMessage : ValueChangedMessage<string>
{
    public LanguageChangedMessage(string language) : base(language) { }
}
