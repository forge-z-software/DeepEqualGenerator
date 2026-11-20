# HTML diff reports

Save the changes between two .NET model snapshots as a standalone HTML file. The report shows before and after values beside each other and identifies changes by property, index, or business key. Open the file in a browser to filter by path or change type. The table also works with JavaScript disabled.

```csharp
using DeepEqual;
using DeepEqual.Generator.Shared;

var format = new ChangeFormatOptions
{
    RedactPath = path => path == "$.SupplierReferences" ||
        path.StartsWith("$.SupplierReferences[", StringComparison.Ordinal)
};

var report = expected.DescribeDeepChanges(actual);
File.WriteAllText("inventory-diff.html", report.ToHtml(format));
```

`RedactPath` receives the full report path. Match the sensitive member and its descendants when the rule covers a subtree. Paths can contain dictionary keys or business keys, so matched rows hide the entire path as well as the before and after values. Matched runtime fallbacks hide the path and runtime type. Review which paths your model generates before sharing a report. Set `IncludeValues = false` to suppress all before and after values while keeping the redaction rule for sensitive paths. `MaxValueLength` truncates long string values before quoting and escaping.

## Attach a report to a failed test

```csharp
try
{
    expected.AssertDeepEqual(actual, format: format);
}
catch (DeepComparisonException failure)
{
    Directory.CreateDirectory("artifacts/deep-equal");
    File.WriteAllText("artifacts/deep-equal/inventory-diff.html", failure.ToHtml());
    throw;
}
```

`failure.ToHtml()` uses the formatting and redaction captured when the test failed. `failure.Report` contains the underlying change data and needs its own formatting options if you export it separately.

The [GitHub Actions example](../.github/workflows/html-report-example.yml) builds the NuGet candidate, restores the [inventory test project](../samples/HtmlReportCi), and uploads the report when the test fails. Run **HTML diff report example** in Actions with **Show a failed test with a redacted diff artifact** enabled. Download **deep-equal-report** from the run, extract it, and open `inventory-diff.html`. To adapt the workflow, copy the failure handler into your test and point the upload step at your report directory. Use a unique filename per test if several tests write to the same directory.

## Run the example locally

From the repository root, run these commands in PowerShell 7.

```powershell
dotnet pack DeepEqual.Generator.Shared -c Release -o artifacts/packages
dotnet restore samples/HtmlReportCi --configfile samples/HtmlReportCi/LocalPackage.NuGet.Config --packages artifacts/html-cache
./samples/HtmlReportCi/Verify-Report.ps1
```

The verification script runs both cases and checks that the failing test writes only the intended changes. To keep the example report, run the deliberate mismatch directly. This command exits with a test failure and saves `artifacts/deep-equal/inventory-diff.html`.

```powershell
$env:DEEPEQUAL_DEMO_MISMATCH = '1'
$env:DEEPEQUAL_REPORT_DIRECTORY = Join-Path (Get-Location).Path 'artifacts/deep-equal'
dotnet test samples/HtmlReportCi -c Release --no-restore
Remove-Item Env:DEEPEQUAL_DEMO_MISMATCH, Env:DEEPEQUAL_REPORT_DIRECTORY
```
