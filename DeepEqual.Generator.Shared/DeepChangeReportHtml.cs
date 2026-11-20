using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;

namespace DeepEqual;

internal static class DeepChangeReportHtml
{
    private const string Styles = """
        :root {
          color-scheme: light dark;
          --surface: #fff; --background: #f3f6fa; --ink: #172033;
          --muted: #475569; --border: #cbd5e1; --accent: #2153a1;
          --before: #fff1f0; --after: #edf8f0; --notice: #fff6da;
        }
        @media (prefers-color-scheme: dark) {
          :root {
            --surface: #172132; --background: #101826; --ink: #f1f5f9;
            --muted: #b7c4d6; --border: #44536a; --accent: #a4c7ff;
            --before: #3a252b; --after: #20382f; --notice: #403321;
          }
        }
        * { box-sizing: border-box; }
        [hidden] { display: none !important; }
        body {
          margin: 0; background: var(--background); color: var(--ink);
          font: 1rem/1.55 system-ui, sans-serif;
        }
        main { max-width: 76rem; margin: auto; padding: clamp(1rem, 4vw, 3rem); }
        h1 { margin: 0; font-size: clamp(1.8rem, 4vw, 2.7rem); line-height: 1.15; letter-spacing: -.035em; }
        header p { margin: .5rem 0 1.4rem; color: var(--muted); }
        .panel { background: var(--surface); border: 1px solid var(--border); border-radius: .65rem; }
        .notice { background: var(--notice); border-left: .25rem solid var(--accent); padding: .6rem .9rem; }
        .filters { display: flex; flex-wrap: wrap; gap: .8rem 1rem; align-items: end; padding: 1rem; }
        .filters label { display: block; font-size: .875rem; font-weight: 650; }
        .search { flex: 1 1 14rem; }
        input, select {
          width: 100%; min-height: 2.5rem; margin-top: .25rem; padding: .35rem .6rem;
          font: inherit; color: inherit; background: var(--surface); border: 1px solid var(--border); border-radius: .35rem;
        }
        output { color: var(--muted); padding: .4rem 0; font-size: .875rem; }
        :focus-visible { outline: .15rem solid var(--accent); outline-offset: .15rem; }
        .comparison { overflow-x: auto; }
        table { width: 100%; min-width: 34rem; border-collapse: collapse; table-layout: fixed; }
        caption { text-align: left; padding: .75rem 1rem; font-weight: 650; }
        th, td { border-top: 1px solid var(--border); padding: .65rem .8rem; text-align: left; vertical-align: top; }
        thead th { font-size: .875rem; color: var(--muted); }
        tbody th { font-weight: normal; }
        .path-col { width: 31%; }
        .kind-col { width: 15%; }
        .value-col { width: 27%; }
        .before { background: var(--before); }
        .after { background: var(--after); }
        code { font: .875rem/1.5 ui-monospace, Consolas, monospace; white-space: pre-wrap; overflow-wrap: anywhere; }
        .qualifier { color: var(--muted); font-size: .875rem; }
        .empty, details { padding: .8rem 1rem; }
        details { margin-top: 1rem; }
        summary { cursor: pointer; font-weight: 650; }
        details ul { padding-left: 1.2rem; }
        @media print {
          :root { color-scheme: light; --surface: #fff; --background: #fff; --ink: #000; --muted: #444; --border: #aaa; }
          main { padding: 0; }
          .filters { display: none !important; }
          table { min-width: 0; }
          tr { break-inside: avoid; }
        }
        """;

    // Browsers normalize line endings in inline scripts before checking CSP hashes.
    private static readonly string FilterScript = """
        (() => {
          const controls = document.getElementById('filters');
          if (!controls) return;
          const path = document.getElementById('filter-path');
          const kind = document.getElementById('filter-kind');
          const rows = Array.from(document.querySelectorAll('#changes tbody tr'));
          const count = document.getElementById('filter-count');
          const empty = document.getElementById('filter-empty');
          function filter() {
            const query = path.value.trim().toLowerCase();
            let visible = 0;
            for (const row of rows) {
              row.hidden = (kind.value !== '' && row.dataset.kind !== kind.value)
                || !row.cells[0].textContent.toLowerCase().includes(query);
              if (!row.hidden) visible++;
            }
            count.textContent = visible + ' of ' + rows.length + ' shown';
            empty.hidden = visible !== 0;
          }
          path.addEventListener('input', filter);
          kind.addEventListener('change', filter);
          filter();
          controls.hidden = false;
        })();
        """.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static readonly string ScriptHash =
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(FilterScript)));

    private static readonly string[] Labels = ["Changed", "Added", "Removed", "Moved", "Count", "Set"];

    internal static string Render(ReportPresentation report)
    {
        var html = new StringBuilder(Styles.Length + FilterScript.Length + 2048);
        html.Append("""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            """);
        html.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; script-src 'sha256-")
            .Append(ScriptHash).Append("'; base-uri 'none'; form-action 'none'\">");
        html.Append("<title>Deep comparison report</title><style>").Append(Styles).Append("</style></head>");
        html.Append("<body><main><header><h1>Deep comparison report</h1><p>")
            .Append(report.Changes.Length.ToString(CultureInfo.InvariantCulture))
            .Append(report.Changes.Length == 1 ? " change</p></header>" : " changes</p></header>");

        if (report.IsTruncated)
            html.Append("<p class=\"notice\">Additional changes omitted because the report reached its limit.</p>");
        if (report.Changes.Length == 0)
            html.Append("<p class=\"panel empty\">No changes found.</p>");
        else
            RenderChanges(html, report.Changes);
        if (report.Fallbacks.Length != 0)
            RenderFallbacks(html, report.Fallbacks);
        html.Append("</main>");
        if (report.Changes.Length != 0)
            html.Append("<script>").Append(FilterScript).Append("</script>");
        return html.Append("</body></html>").ToString();
    }

    private static void RenderChanges(StringBuilder html, DisplayChange[] changes)
    {
        html.Append("""
            <section class="panel" aria-label="Changes">
            <div class="filters" id="filters" hidden>
              <div class="search"><label for="filter-path">Filter by path</label>
              <input type="search" id="filter-path" autocomplete="off" spellcheck="false"></div>
              <div><label for="filter-kind">Change type</label>
              <select id="filter-kind"><option value="">All changes</option>
            """);
        foreach (var kind in Labels)
        {
            if (Array.Exists(changes, change => Label(change.Kind) == kind))
                html.Append("<option>").Append(kind).Append("</option>");
        }
        html.Append("""
            </select></div><output id="filter-count" role="status"></output></div>
            <p id="filter-empty" class="empty" hidden>No changes match these filters.</p>
            <div class="comparison" tabindex="0" role="region" aria-label="Before and after comparison">
            <table id="changes">
              <caption>Changes by path</caption>
              <thead><tr><th class="path-col" scope="col">Path</th><th class="kind-col" scope="col">Change</th>
              <th class="value-col" scope="col">Before</th><th class="value-col" scope="col">After</th></tr></thead>
              <tbody>
            """);
        foreach (var change in changes)
        {
            var kind = Label(change.Kind);
            html.Append("<tr data-kind=\"").Append(kind).Append("\"><th scope=\"row\"><code>");
            AppendEncoded(html, change.Path);
            html.Append("</code></th><td>").Append(kind).Append("</td>");
            RenderValue(html, "before", change.Before, change.IsRedacted);
            RenderValue(html, "after", change.After, change.IsRedacted);
            html.Append("</tr>");
        }
        html.Append("</tbody></table></div></section>");
    }

    private static void RenderValue(StringBuilder html, string side, string? value, bool redacted)
    {
        html.Append("<td class=\"").Append(side).Append("\">");
        if (value is not null)
        {
            html.Append("<code>");
            AppendEncoded(html, value);
            html.Append("</code>");
        }
        else
        {
            html.Append(redacted ? "<span class=\"qualifier\">Redacted</span>" :
                "<span class=\"qualifier\" title=\"Value unavailable or hidden\">—</span>");
        }
        html.Append("</td>");
    }

    private static void RenderFallbacks(StringBuilder html, DisplayFallback[] fallbacks)
    {
        html.Append("<details class=\"panel\"><summary>Runtime equality fallbacks</summary><ul>");
        foreach (var fallback in fallbacks)
        {
            html.Append("<li><code>");
            AppendEncoded(html, fallback.Path);
            html.Append("</code>");
            if (fallback.TypeName is not null)
            {
                html.Append(" <span class=\"qualifier\">");
                AppendEncoded(html, fallback.TypeName);
                html.Append("</span>");
            }
            html.Append("</li>");
        }
        html.Append("</ul></details>");
    }

    private static void AppendEncoded(StringBuilder html, string value) => html.Append(HtmlEncoder.Default.Encode(value));

    private static string Label(DeepMismatchKind kind) => kind switch
    {
        DeepMismatchKind.Added => "Added",
        DeepMismatchKind.Removed or DeepMismatchKind.MissingKey => "Removed",
        DeepMismatchKind.Moved => "Moved",
        DeepMismatchKind.Count => "Count",
        DeepMismatchKind.Set => "Set",
        _ => "Changed"
    };
}
