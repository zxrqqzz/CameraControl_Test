using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

// WPF 与 Markdig 都有 Block / Table / TableRow / TableCell，必须显式区分
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using WpfList = System.Windows.Documents.List;
using WpfTable = System.Windows.Documents.Table;
using WpfTableCell = System.Windows.Documents.TableCell;
using WpfTableRow = System.Windows.Documents.TableRow;

namespace CameraControl.Presentation.Markdown;

/// <summary>
/// Markdown → WPF FlowDocument 渲染器。
///
/// 分工：**解析交给 Markdig**（嵌套/转义/表格这些脏活不值得重写），
/// **渲染完全自己实现**——要嵌进现有界面、要控制样式，第三方渲染器给的是它自己的控件。
///
/// 支持：标题 / 段落 / 粗体 / 斜体 / 行内代码 / 代码块 / 无序列表 / 有序列表 /
/// 引用 / 分隔线 / 表格 / 链接（仅 http/https 可点击）。
///
/// 安全：
/// - <c>DisableHtml()</c>：模型输出是不可信内容，原始 HTML 一律当纯文本，避免注入；
/// - 链接只放行 http/https，绝不放行 file:、javascript:、UNC 路径。
/// </summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .DisableHtml()
        .Build();

    private static readonly FontFamily Monospace = new("Consolas, Cascadia Mono, Courier New");

    private static readonly Brush CodeBackground = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));

    /// <summary>把 Markdown 渲染成可显示的文档。空输入返回空文档（不抛）。</summary>
    public static FlowDocument Render(string? markdown, double baseFontSize = 13)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontSize = baseFontSize,
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
            Foreground = Brushes.Black
        };

        if (string.IsNullOrWhiteSpace(markdown)) return document;

        MarkdownDocument parsed;

        try
        {
            parsed = global::Markdig.Markdown.Parse(markdown, Pipeline);
        }
        catch (Exception)
        {
            // 解析器不该抛；真抛了退回纯文本，绝不因渲染崩界面
            document.Blocks.Add(new Paragraph(new Run(markdown)));
            return document;
        }

        RenderBlocks(document.Blocks, parsed, baseFontSize);

        return document;
    }

    // ── 块级 ────────────────────────────────────────────────

    private static void RenderBlocks(BlockCollection target, IEnumerable<MdBlock> blocks, double baseSize)
    {
        foreach (MdBlock block in blocks)
        {
            switch (block)
            {
                case HeadingBlock heading:
                {
                    var paragraph = new Paragraph
                    {
                        FontWeight = FontWeights.SemiBold,
                        FontSize = baseSize + (heading.Level switch { 1 => 6, 2 => 4, 3 => 2, _ => 1 }),
                        Margin = new Thickness(0, 10, 0, 4)
                    };

                    if (heading.Inline is not null) AppendInlines(paragraph.Inlines, heading.Inline, baseSize);
                    target.Add(paragraph);
                    break;
                }

                case ParagraphBlock paragraphBlock:
                {
                    var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };

                    if (paragraphBlock.Inline is not null)
                        AppendInlines(paragraph.Inlines, paragraphBlock.Inline, baseSize);

                    target.Add(paragraph);
                    break;
                }

                case ListBlock list:
                    target.Add(RenderList(list, baseSize));
                    break;

                case QuoteBlock quote:
                {
                    var paragraph = new Paragraph
                    {
                        Margin = new Thickness(0, 0, 0, 8),
                        Padding = new Thickness(10, 4, 0, 4),
                        BorderThickness = new Thickness(3, 0, 0, 0),
                        BorderBrush = Muted,
                        Foreground = Muted
                    };

                    foreach (MdBlock child in quote)
                    {
                        if (child is LeafBlock { Inline: not null } leaf)
                            AppendInlines(paragraph.Inlines, leaf.Inline, baseSize);
                    }

                    target.Add(paragraph);
                    break;
                }

                case FencedCodeBlock fenced:
                    target.Add(RenderCode(fenced.Lines.ToString(), baseSize));
                    break;

                case CodeBlock codeBlock:
                    target.Add(RenderCode(codeBlock.Lines.ToString(), baseSize));
                    break;

                case ThematicBreakBlock:
                    target.Add(new Paragraph
                    {
                        Margin = new Thickness(0, 8, 0, 8),
                        BorderThickness = new Thickness(0, 1, 0, 0),
                        BorderBrush = CodeBackground
                    });
                    break;

                case MdTable table:
                    target.Add(RenderTable(table, baseSize));
                    break;

                default:
                    if (block is LeafBlock { Lines.Count: > 0 } plain)
                        target.Add(new Paragraph(new Run(plain.Lines.ToString())) { Margin = new Thickness(0, 0, 0, 8) });
                    break;
            }
        }
    }

    private static WpfList RenderList(ListBlock list, double baseSize)
    {
        var wpfList = new WpfList
        {
            MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(20, 0, 0, 8),
            Padding = new Thickness(0)
        };

        foreach (MdBlock item in list)
        {
            if (item is not ListItemBlock listItem) continue;

            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };

            foreach (MdBlock child in listItem)
            {
                if (child is LeafBlock { Inline: not null } leaf)
                    AppendInlines(paragraph.Inlines, leaf.Inline, baseSize);
            }

            wpfList.ListItems.Add(new ListItem(paragraph));
        }

        return wpfList;
    }

    private static Paragraph RenderCode(string code, double baseSize) =>
        new(new Run(code.TrimEnd('\r', '\n')))
        {
            FontFamily = Monospace,
            FontSize = baseSize - 1,
            Background = CodeBackground,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 8)
        };

    private static WpfTable RenderTable(MdTable table, double baseSize)
    {
        var wpfTable = new WpfTable
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 0, 0, 8)
        };

        int columnCount = table
            .OfType<MdTableRow>()
            .Select(r => r.Count)
            .DefaultIfEmpty(0)
            .Max();

        for (int i = 0; i < columnCount; i++) wpfTable.Columns.Add(new TableColumn());

        var group = new TableRowGroup();

        foreach (MdTableRow row in table.OfType<MdTableRow>())
        {
            var wpfRow = new WpfTableRow();

            if (row.IsHeader) wpfRow.Background = CodeBackground;

            foreach (MdTableCell cell in row.OfType<MdTableCell>())
            {
                var paragraph = new Paragraph
                {
                    Margin = new Thickness(0),
                    FontWeight = row.IsHeader ? FontWeights.SemiBold : FontWeights.Normal
                };

                foreach (MdBlock child in cell)
                {
                    if (child is LeafBlock { Inline: not null } leaf)
                        AppendInlines(paragraph.Inlines, leaf.Inline, baseSize);
                }

                wpfRow.Cells.Add(new WpfTableCell(paragraph)
                {
                    BorderBrush = CodeBackground,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(6, 3, 6, 3)
                });
            }

            group.Rows.Add(wpfRow);
        }

        wpfTable.RowGroups.Add(group);

        return wpfTable;
    }

    // ── 行内 ────────────────────────────────────────────────

    private static void AppendInlines(InlineCollection target, ContainerInline container, double baseSize)
    {
        foreach (MdInline inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString()));
                    break;

                case EmphasisInline emphasis:
                {
                    var span = new Span();

                    if (emphasis.DelimiterCount >= 2) span.FontWeight = FontWeights.SemiBold;
                    if (emphasis.DelimiterCount == 1) span.FontStyle = FontStyles.Italic;

                    AppendInlines(span.Inlines, emphasis, baseSize);
                    target.Add(span);
                    break;
                }

                case CodeInline code:
                    target.Add(new Run(code.Content)
                    {
                        FontFamily = Monospace,
                        FontSize = baseSize - 1,
                        Background = CodeBackground
                    });
                    break;

                case LinkInline link when IsSafeLink(link.Url):
                {
                    var hyperlink = new Hyperlink();
                    AppendInlines(hyperlink.Inlines, link, baseSize);

                    string url = link.Url!;
                    hyperlink.NavigateUri = new Uri(url);
                    hyperlink.RequestNavigate += (_, e) => OpenLink(url, e);

                    target.Add(hyperlink);
                    break;
                }

                case LinkInline link:
                    // 不安全的链接只显示文字，不可点
                    AppendInlines(target, link, baseSize);
                    break;

                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;

                case ContainerInline nested:
                    AppendInlines(target, nested, baseSize);
                    break;
            }
        }
    }

    /// <summary>只放行 http/https：模型输出里的链接不可信。</summary>
    private static bool IsSafeLink(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static void OpenLink(string url, RequestNavigateEventArgs e)
    {
        e.Handled = true;

        if (!IsSafeLink(url)) return;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // 打不开浏览器不影响阅读
        }
    }
}
