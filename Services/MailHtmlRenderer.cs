using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using GLook.Models;
using HtmlAgilityPack;

namespace GLook.Services;

public static partial class MailHtmlRenderer
{
    private static readonly HashSet<string> RemovedElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "iframe", "object", "embed", "applet", "frame", "frameset",
        "form", "input", "button", "textarea", "select", "option", "meta", "base", "link"
    };

    public static bool ContainsRemoteContent(MailThreadDetail? detail) =>
        detail?.Messages.Any(message =>
            !string.IsNullOrWhiteSpace(message.BodyHtml)
            && RemoteContentPattern().IsMatch(message.BodyHtml)) == true;

    public static string BuildDocument(MailThreadDetail detail, bool allowRemoteContent)
    {
        ArgumentNullException.ThrowIfNull(detail);

        var html = new StringBuilder(16_384);
        html.Append("<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"color-scheme\" content=\"light\">");
        html.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"");
        html.Append(allowRemoteContent
            ? "default-src 'none'; img-src data: https: http:; style-src 'unsafe-inline'; font-src data: https: http:; media-src 'none'; frame-src 'none'; object-src 'none'; form-action 'none'; connect-src 'none'"
            : "default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src data:; media-src 'none'; frame-src 'none'; object-src 'none'; form-action 'none'; connect-src 'none'");
        html.Append("\"><style>");
        html.Append("html,body{margin:0;padding:0;background:#fff;color:#202124;font:14px/1.55 'Segoe UI',Arial,sans-serif;overflow-wrap:anywhere}");
        html.Append("body{padding:20px 28px}.message{padding:0 0 26px;margin:0 0 26px;border-bottom:1px solid #e0e3e7}.message:last-child{border-bottom:0}");
        html.Append(".header{display:grid;grid-template-columns:minmax(0,1fr) auto;gap:4px 16px;margin-bottom:16px}.from{font-weight:600;font-size:15px}.meta,.date{color:#5f6368;font-size:12px}.date{text-align:right}.body{min-width:0}.body img{max-width:100%;height:auto}.body table{max-width:100%}.plain{white-space:pre-wrap;font:15px/1.55 'Segoe UI',Arial,sans-serif;margin:0}");
        html.Append("a{color:#0b57d0}blockquote{border-left:3px solid #dadce0;margin-left:0;padding-left:14px;color:#5f6368}</style></head><body>");

        foreach (var message in detail.Messages)
        {
            html.Append("<article class=\"message\"><header class=\"header\"><div><div class=\"from\">");
            html.Append(WebUtility.HtmlEncode(message.From));
            html.Append("</div><div class=\"meta\">To: ");
            html.Append(WebUtility.HtmlEncode(message.To));
            if (!string.IsNullOrWhiteSpace(message.Cc))
            {
                html.Append("<br>Cc: ");
                html.Append(WebUtility.HtmlEncode(message.Cc));
            }

            html.Append("</div></div><time class=\"date\">");
            html.Append(WebUtility.HtmlEncode(message.SentText));
            html.Append("</time></header><section class=\"body\">");
            if (!message.IsOmissionNotice && !string.IsNullOrWhiteSpace(message.BodyHtml))
            {
                html.Append(SanitizeBody(message.BodyHtml, allowRemoteContent));
            }
            else
            {
                html.Append("<pre class=\"plain\">");
                html.Append(WebUtility.HtmlEncode(message.BodyText));
                html.Append("</pre>");
            }

            html.Append("</section></article>");
        }

        html.Append("</body></html>");
        return html.ToString();
    }

    private static string SanitizeBody(string source, bool allowRemoteContent)
    {
        var document = new HtmlDocument
        {
            OptionFixNestedTags = true,
            OptionAutoCloseOnEnd = true
        };
        document.LoadHtml(source);

        foreach (var node in document.DocumentNode.Descendants().ToList())
        {
            if (RemovedElements.Contains(node.Name))
            {
                node.Remove();
                continue;
            }

            foreach (var attribute in node.Attributes.ToList())
            {
                var name = attribute.Name;
                if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("srcdoc", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("action", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("formaction", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("xlink:href", StringComparison.OrdinalIgnoreCase))
                {
                    node.Attributes.Remove(attribute);
                    continue;
                }

                if (name.Equals("href", StringComparison.OrdinalIgnoreCase)
                    && !IsSafeLink(attribute.Value ?? string.Empty))
                {
                    node.Attributes.Remove(attribute);
                    continue;
                }

                if (name.Equals("src", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("background", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("poster", StringComparison.OrdinalIgnoreCase))
                {
                    if (!IsSafeResource(attribute.Value ?? string.Empty, allowRemoteContent))
                    {
                        node.Attributes.Remove(attribute);
                    }

                    continue;
                }

                if (name.Equals("srcset", StringComparison.OrdinalIgnoreCase))
                {
                    if (!allowRemoteContent)
                    {
                        node.Attributes.Remove(attribute);
                    }

                    continue;
                }

                if (name.Equals("style", StringComparison.OrdinalIgnoreCase))
                {
                    attribute.Value = SanitizeCss(attribute.Value ?? string.Empty, allowRemoteContent);
                }
            }

            if (node.Name.Equals("style", StringComparison.OrdinalIgnoreCase))
            {
                node.InnerHtml = SanitizeCss(node.InnerHtml, allowRemoteContent);
            }
        }

        var body = document.DocumentNode.SelectSingleNode("//body");
        return body?.InnerHtml ?? document.DocumentNode.InnerHtml;
    }

    private static bool IsSafeLink(string value)
    {
        var decoded = HtmlEntity.DeEntitize(value).Trim();
        if (decoded.StartsWith('#'))
        {
            return true;
        }

        return Uri.TryCreate(decoded, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" or "mailto";
    }

    private static bool IsSafeResource(string value, bool allowRemoteContent)
    {
        var decoded = HtmlEntity.DeEntitize(value).Trim();
        if (decoded.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return allowRemoteContent
            && Uri.TryCreate(decoded, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https";
    }

    private static string SanitizeCss(string css, bool allowRemoteContent)
    {
        var sanitized = DangerousCssPattern().Replace(css, string.Empty);
        sanitized = CssImportPattern().Replace(sanitized, string.Empty);
        return allowRemoteContent ? sanitized : CssUrlPattern().Replace(sanitized, "none");
    }

    [GeneratedRegex("(?i)(?:<(?:img|source|body|table|td)[^>]+(?:src|srcset|background)\\s*=\\s*['\"]?\\s*(?:https?:)?//|url\\s*\\(\\s*['\"]?\\s*(?:https?:)?//)")]
    private static partial Regex RemoteContentPattern();

    [GeneratedRegex("(?is)(?:expression\\s*\\(|javascript\\s*:|behavior\\s*:|-moz-binding\\s*:)")]
    private static partial Regex DangerousCssPattern();

    [GeneratedRegex("(?is)@import[^;]+;?")]
    private static partial Regex CssImportPattern();

    [GeneratedRegex("(?is)url\\s*\\([^)]*\\)")]
    private static partial Regex CssUrlPattern();
}
