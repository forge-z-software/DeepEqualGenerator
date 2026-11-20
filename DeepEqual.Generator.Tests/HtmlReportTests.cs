using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DeepEqual.Generator.Shared;

namespace DeepEqual;

public sealed class HtmlReportTests
{
    [Fact]
    public void HtmlShowsChangesInBeforeAndAfterColumns()
    {
        var before = new WorkflowInventory { Lines = [new("SKU-9", 2), new("SKU-11", 1)] };
        var after = new WorkflowInventory { Lines = [new("SKU-9", 3), new("SKU-12", 1)] };
        var html = before.DescribeDeepChanges(after).ToHtml();

        Assert.Contains("scope=\"col\">Before", html);
        Assert.Contains("scope=\"col\">After", html);
        Assert.Contains("$.Lines[&quot;SKU-9&quot;].Quantity</code></th><td>Changed</td>", html);
        Assert.Contains("<td class=\"before\"><code>2</code></td><td class=\"after\"><code>3</code></td>", html);
        Assert.Contains("<tr data-kind=\"Added\">", html);
        Assert.Contains("<tr data-kind=\"Removed\">", html);
        Assert.Contains("for=\"filter-path\"", html);
        Assert.Contains("for=\"filter-kind\"", html);
    }

    [Fact]
    public void HtmlKeepsRedactedPathsAndValuesOutOfTheEntireDocument()
    {
        var before = new WorkflowInventory { Locations = new() { ["private-key"] = 587531, ["public"] = 1 } };
        var after = new WorkflowInventory { Locations = new() { ["private-key"] = 894621, ["public"] = 2 } };
        var report = before.DescribeDeepChanges(after);
        var html = WebUtility.HtmlDecode(report.ToHtml(new() { RedactPath = path => path.Contains("private-key", StringComparison.Ordinal) }));

        Assert.DoesNotContain("private-key", html);
        Assert.DoesNotContain("587531", html);
        Assert.DoesNotContain("894621", html);
        Assert.Contains("[redacted]", html);
        Assert.Contains("Redacted</span>", html);
        Assert.Contains("$.Locations[\"public\"]", html);
    }

    [Fact]
    public void AssertionHtmlKeepsTheRedactionCapturedAtTheTimeOfFailure()
    {
        var before = new WorkflowInventory { Name = "original-secret", Locations = new() { ["A"] = 1 } };
        var after = new WorkflowInventory { Name = "changed-secret", Locations = new() { ["A"] = 2 } };
        var redactName = true;
        var options = new ChangeFormatOptions
        {
            RedactPath = path => redactName && path == "$.Name"
        };
        var failure = Assert.Throws<DeepComparisonException>(() => before.AssertDeepEqual(after, format: options));
        redactName = false;

        var html = failure.ToHtml();
        Assert.DoesNotContain("secret", failure.Message);
        Assert.DoesNotContain("secret", html);
        Assert.DoesNotContain("$.Name", html);
        Assert.Contains("[redacted]", html);
        Assert.Contains("<td class=\"after\"><code>2</code></td>", html);
    }

    [Fact]
    public void SharedRedactionDecisionCoversAllEntriesAtTheSamePath()
    {
        var context = ComparisonContext.ForReport(options: null, maxChanges: 4);
        using (context.AtMember("private-path"))
        {
            context.RecordMismatch("first-secret", "second-secret", DeepMismatchKind.Moved);
            context.RecordMismatch("third-secret", "fourth-secret");
        }

        var calls = 0;
        var html = context.GetReport().ToHtml(new ChangeFormatOptions { RedactPath = _ => ++calls == 1 });
        Assert.Equal(1, calls);
        Assert.DoesNotContain("secret", html);
        Assert.DoesNotContain("private-path", html);
        Assert.Equal(2, Regex.Matches(html, @"<code>\[redacted\]</code>").Count);
    }

    [Fact]
    public void RedactionHidesRuntimeFallbackPathsAndTypeNames()
    {
        var before = new WorkflowDynamic { Item = new FallbackToken(1) };
        var after = new WorkflowDynamic { Item = new FallbackToken(2) };
        var report = before.DescribeDeepChanges(after);

        Assert.Contains(nameof(FallbackToken), report.ToHtml());
        var options = new ChangeFormatOptions { RedactPath = path => path == "$.Item" };
        var html = report.ToHtml(options);
        Assert.Contains("Runtime equality fallbacks", html);
        Assert.DoesNotContain("$.Item", html);
        Assert.DoesNotContain(nameof(FallbackToken), html);
    }

    [Fact]
    public void UntrustedPathsStayTextAndTheInlineFilterHasAnExactContentHash()
    {
        const string Path = "path\"><img src=x onerror=alert(1)>";
        var context = ComparisonContext.ForReport(options: null, maxChanges: 1);
        using (context.AtMember(Path))
            context.RecordMismatch("</script><script>alert(2)</script>", "&lt;safe&gt;");

        var html = context.GetReport().ToHtml();
        var scripts = Regex.Matches(html, @"<script>([\s\S]*?)</script>");
        var script = Assert.Single(scripts.Cast<Match>()).Groups[1].Value;
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(script)));

        Assert.Contains($"script-src 'sha256-{hash}'", html);
        Assert.Contains("default-src 'none'", html);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("<script>alert", html);
        Assert.Contains(Path, WebUtility.HtmlDecode(html));
    }

    [Fact]
    public void HtmlHonorsValueSuppressionAndTheLengthLimit()
    {
        var before = new WorkflowInventory { Name = "😀private-before" };
        var after = new WorkflowInventory { Name = "😀private-after" };
        var report = before.DescribeDeepChanges(after);

        var bounded = report.ToHtml(new() { MaxValueLength = 1 });
        Assert.DoesNotContain("private", bounded);
        Assert.Contains(@"\u2026", bounded);
        var hidden = report.ToHtml(new() { IncludeValues = false });
        Assert.Contains("$.Name", hidden);
        Assert.DoesNotContain("private", hidden);
        Assert.DoesNotContain("<td class=\"before\"><code>", hidden);
        Assert.DoesNotContain("<td class=\"after\"><code>", hidden);

        var nullReport = new WorkflowDynamic().DescribeDeepChanges(new WorkflowDynamic { Item = "present" }).ToHtml();
        Assert.Contains("<td class=\"before\"><code>null</code></td>", nullReport);
    }

    [Fact]
    public void EmptyAndTruncatedReportsDescribeTheAvailableResults()
    {
        var before = new WorkflowInventory();
        var empty = before.DescribeDeepChanges(before).ToHtml();
        Assert.Contains("No changes found.", empty);
        Assert.DoesNotContain("<script>", empty);

        var truncated = before.DescribeDeepChanges(new WorkflowInventory { Locations = new() { ["A"] = 1, ["B"] = 2 } },
            maxChanges: 1).ToHtml();
        Assert.Contains("Additional changes omitted", truncated);
        Assert.Contains("1 change</p>", truncated);
        Assert.Single(Regex.Matches(truncated, "<tr data-kind=").Cast<Match>());
    }

    private sealed record FallbackToken(int Number);
}
