using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-markdown text="{{Doc}}"&gt;</c> (or raw markdown as the element's text) — renders a small
/// Markdown subset: <c>#</c>/<c>##</c>/<c>###</c> headings, <c>**bold**</c>, <c>*italic*</c>/<c>_italic_</c>,
/// inline <c>`code`</c> and fenced <c>```</c> code blocks, <c>- </c>/<c>* </c> bullet lists, <c>[text](url)</c>
/// links, and blank-line-separated paragraphs. It parses to the toolkit's own elements (which then lay out
/// + paint like any other markup), so no HTML/DOM is injected raw.
/// </summary>
public sealed partial class MarkdownComponent : ComponentBase
{
    public override string Tag => "cupri-markdown";
    public override string DefaultCss => """
        .cupri-md { display:block; color:var(--cupri-text,#1e2430); font-size:15px; line-height:1.7; }
        .cupri-md h1 { font-size:25px; font-weight:bold; margin:2px 0 12px; }
        .cupri-md h2 { font-size:19px; font-weight:bold; margin:18px 0 8px; }
        .cupri-md h3 { font-size:16px; font-weight:bold; margin:14px 0 6px; }
        .cupri-md p { margin:0 0 11px; }
        .cupri-md ul { display:block; margin:0 0 11px; padding-left:8px; }
        .cupri-md li { display:block; margin:4px 0; }
        .cupri-md-bull { display:inline-block; width:16px; color:var(--cupri-muted,#98a2b3); }
        .cupri-md a { color:var(--cupri-accent,#B87333); }
        .cupri-md strong { font-weight:bold; }
        .cupri-md code { font-family:monospace; font-size:0.9em; background:var(--cupri-hover,#eef1f5);
                         border:1px solid var(--cupri-border,#e6e9f0); border-radius:5px; padding:1px 5px; }
        .cupri-md pre { background:var(--cupri-hover,#eef1f5); border:1px solid var(--cupri-border,#e6e9f0);
                        border-radius:8px; padding:12px 14px; margin:0 0 11px; font-family:monospace;
                        font-size:13px; line-height:1.5; overflow:auto; }
        .cupri-md-cl { display:block; }
        """;

    public override void Expand(IElement el)
    {
        el.ClassList.Add("cupri-md");
        var src = Str(el, "text");
        if (src.Length == 0) src = Dedent(el.TextContent);
        el.InnerHtml = Render(src);
    }

    private static string Render(string md)
    {
        var sb = new StringBuilder();
        var lines = md.Replace("\r\n", "\n").Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            var t = lines[i].TrimStart();
            if (t.StartsWith("```"))                              // fenced code block (verbatim)
            {
                i++;
                sb.Append("<pre>");                               // one block per line — text nodes collapse
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```")) // whitespace + honour no
                { sb.Append("<div class='cupri-md-cl'>").Append(CodeLine(lines[i])).Append("</div>"); i++; } // white-space:pre
                i++;
                sb.Append("</pre>");
            }
            else if (t.StartsWith("### ")) { sb.Append("<h3>").Append(Inline(t[4..])).Append("</h3>"); i++; }
            else if (t.StartsWith("## ")) { sb.Append("<h2>").Append(Inline(t[3..])).Append("</h2>"); i++; }
            else if (t.StartsWith("# ")) { sb.Append("<h1>").Append(Inline(t[2..])).Append("</h1>"); i++; }
            else if (IsBullet(t))                                 // bullet list (consecutive - / * lines)
            {
                sb.Append("<ul>");
                while (i < lines.Length && IsBullet(lines[i].TrimStart()))
                { sb.Append("<li><span class='cupri-md-bull'>&#8226;</span>").Append(Inline(lines[i].TrimStart()[2..])).Append("</li>"); i++; }
                sb.Append("</ul>");
            }
            else if (t.Length == 0) i++;                          // blank line
            else                                                  // paragraph (join consecutive plain lines)
            {
                var para = new StringBuilder();
                while (i < lines.Length && lines[i].TrimStart() is { Length: > 0 } pl
                       && !pl.StartsWith("#") && !IsBullet(pl) && !pl.StartsWith("```"))
                { if (para.Length > 0) para.Append(' '); para.Append(lines[i].Trim()); i++; }
                sb.Append("<p>").Append(Inline(para.ToString())).Append("</p>");
            }
        }
        return sb.ToString();
    }

    private static bool IsBullet(string t) => t.StartsWith("- ") || t.StartsWith("* ");

    // Inline spans. Escape first, then code (its content stays literal), then bold, italic, and links.
    private static string Inline(string s)
    {
        s = Esc(s);
        s = CodeRx().Replace(s, m => "<code>" + m.Groups[1].Value + "</code>");
        s = BoldRx().Replace(s, "<strong>$1</strong>");
        s = ItalicRx().Replace(s, m => "<em>" + (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) + "</em>");
        s = LinkRx().Replace(s, "<a href=\"$2\">$1</a>");
        return s;
    }

    // Strip the common leading indentation (for markdown written inline in the template).
    private static string Dedent(string s)
    {
        var lines = s.Replace("\r\n", "\n").Split('\n');
        var indent = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join('\n', lines.Select(l => l.Length >= indent ? l[indent..] : l)).Trim('\n');
    }

    private static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // One code line: escape, keep leading indentation (spaces collapse otherwise), keep blank lines tall.
    private static string CodeLine(string s)
    {
        var lead = s.Length - s.TrimStart(' ').Length;
        var body = Esc(s.TrimStart(' '));
        return body.Length == 0 ? "&#160;" : string.Concat(Enumerable.Repeat("&#160;", lead)) + body;
    }

    [GeneratedRegex(@"`([^`]+)`")] private static partial Regex CodeRx();
    [GeneratedRegex(@"\*\*([^*]+)\*\*")] private static partial Regex BoldRx();
    [GeneratedRegex(@"(?<!\*)\*(?!\*)([^*]+)\*(?!\*)|_([^_]+)_")] private static partial Regex ItalicRx();
    [GeneratedRegex(@"\[([^\]]+)\]\(([^)]+)\)")] private static partial Regex LinkRx();
}
