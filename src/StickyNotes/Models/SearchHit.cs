using System;
using System.Collections.Generic;
using StickyNotes.Services.Localization;

namespace StickyNotes.Models;

/// <summary>
/// 文本分段（用于搜索高亮渲染）
/// </summary>
public sealed record SnippetSegment(string Text, bool IsHit);

/// <summary>
/// 搜索预览行（用于 1~3 行上下文独立渲染，避免跨行折行错乱）
/// </summary>
public sealed record SnippetLine(IReadOnlyList<SnippetSegment> Segments);

/// <summary>
/// 搜索命中项模型（每张命中便签可产出 1~3 张卡片，展示命中行上下文与更新日期）
/// </summary>
public sealed record SearchHit(
    Guid NoteId,
    string NoteTitle,
    NoteColor Color,
    int LineNumber,        // 逻辑行号（从 1 开始计，合并卡片取组内首命中行）
    int CharIndex,         // 命中选区在 Content 中的绝对起始字符索引
    int Length,            // 命中选区跨度（合并/Tier 2 场景覆盖首词起点至末词终点）
    string LineSnippet,    // 所在行摘要（合并卡片取组内质量最高的代表行）
    string HighlightText,  // 实际命中的文字
    IReadOnlyList<SnippetSegment>? Segments = null, // 兼容扁平分段
    DateTime UpdatedAt = default,                   // 便签更新时间（用于右上角日期展示）
    IReadOnlyList<SnippetLine>? Lines = null,       // 1~3 行上下文独立行列表
    int TotalMatches = 1,                           // 该便签全文总词频
    string DisplayLineNumber = "",                  // 行号胶囊显示文本（"3" 或合并区间 "8-9"）；空值回退 LineNumber
    int Tier = 3                                    // 匹配质量分级：1 完整短语 > 2 同行全词 > 3 部分词/跨行/兜底
)
{
    public IReadOnlyList<SnippetSegment> Segments { get; init; } = Segments ?? Array.Empty<SnippetSegment>();
    public IReadOnlyList<SnippetLine> Lines { get; init; } = Lines ?? Array.Empty<SnippetLine>();
    public DateTime UpdatedAt { get; init; } = UpdatedAt == default ? DateTime.UtcNow : UpdatedAt;

    /// <summary>
    /// 行号胶囊文案：单处命中显示「第 X 行」（合并行为「第 X-Y 行」），
    /// 多处命中显示「第 X 行 · 共 N 处」（N 为全文总词频，与命中卡片数口径不同，故不用伪分页序号）
    /// </summary>
    public string BadgeText
    {
        get
        {
            var lineNo = string.IsNullOrEmpty(DisplayLineNumber) ? LineNumber.ToString() : DisplayLineNumber;
            return TotalMatches > 1
                ? Loc.T("Search.BadgeMulti", "第 {0} 行 · 共 {1} 处", lineNo, TotalMatches)
                : Loc.T("Search.BadgeSingle", "第 {0} 行", lineNo);
        }
    }
}
