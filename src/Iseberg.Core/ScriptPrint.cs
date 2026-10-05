using System.Net;

namespace Iseberg.Core;

public static class ScriptPrint
{
    public static string CreateHtml(string title, string text, string printLabel, string hint)
    {
        string Escape(string value) => WebUtility.HtmlEncode(value);
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; base-uri 'none'; form-action 'none'">
            <title>{{Escape(title)}}</title>
            <style>
            body { margin: 2em; color: black; background: white; }
            pre { white-space: pre-wrap; overflow-wrap: anywhere; tab-size: 4; font: 10pt monospace; }
            h1 { font: bold 12pt sans-serif; }
            @page { margin: 15mm; }
            @media print { nav { display: none; } body { margin: 0; } }
            </style>
            </head>
            <body>
            <nav><button type="button" onclick="window.print()">{{Escape(printLabel)}}</button><p>{{Escape(hint)}}</p></nav>
            <h1>{{Escape(title)}}</h1>
            <pre><code>{{Escape(text)}}</code></pre>
            </body>
            </html>
            """;
    }

    public static async Task WriteAsync(string path, string html)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(path, options);
        await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        await writer.WriteAsync(html);
    }
}
