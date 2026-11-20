using DeepEqual;
using DeepEqual.Generator.Shared;
using Xunit;

namespace HtmlReportCi;

public sealed class InventoryReportTests
{
    [Fact]
    public void InventoryMatchesTheExpectedSnapshot()
    {
        var demonstrateFailure = Environment.GetEnvironmentVariable("DEEPEQUAL_DEMO_MISMATCH") == "1";
        var expected = new Inventory
        {
            Lines = [new("SKU-9", 2), new("SKU-11", 1)],
            SupplierReferences = new() { ["sample-private-key"] = "sample-secret-before" }
        };
        var actual = new Inventory
        {
            Lines = demonstrateFailure ? [new("SKU-9", 3), new("SKU-12", 1)] : [new("SKU-9", 2), new("SKU-11", 1)],
            SupplierReferences = new() { ["sample-private-key"] = demonstrateFailure ? "sample-secret-after" : "sample-secret-before" }
        };

        var format = new ChangeFormatOptions
        {
            RedactPath = path => path == "$.SupplierReferences" ||
                path.StartsWith("$.SupplierReferences[", StringComparison.Ordinal)
        };
        try
        {
            expected.AssertDeepEqual(actual, format: format);
        }
        catch (DeepComparisonException failure)
        {
            var output = Environment.GetEnvironmentVariable("DEEPEQUAL_REPORT_DIRECTORY") ??
                Path.Combine(AppContext.BaseDirectory, "deep-equal-reports");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "inventory-diff.html"), failure.ToHtml());
            throw;
        }
    }
}

[DeepComparable]
public sealed class Inventory
{
    [DeepCompare(KeyMembers = [nameof(InventoryLine.Sku)])]
    public List<InventoryLine> Lines { get; set; } = [];

    public Dictionary<string, string> SupplierReferences { get; set; } = new();
}

public sealed record InventoryLine(string Sku, int Quantity);
