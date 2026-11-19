using DeepEqual;
using DeepEqual.Generator.Shared;

var expected = new Customer { Orders = [new(17) { Lines = [new("SKU-9", 2), new("SKU-8", 1)] }] };
var actual = new Customer { Orders = [new(17) { Lines = [new("SKU-8", 1), new("SKU-9", 3)] }] };

var mismatch = expected.GetDeepMismatch(actual);
if (expected.AreDeepEqual(actual) ||
    mismatch is not { Path: "$.Orders[17].Lines[\"SKU-9\"].Qty", Expected: 2, Actual: 3 })
    throw new InvalidOperationException($"Unexpected comparison result {mismatch}");

Console.WriteLine($"{mismatch.Path} changed from {mismatch.Expected} to {mismatch.Actual}");

[DeepComparable]
public sealed class Customer
{
    [DeepCompare(OrderInsensitive = true, KeyMembers = [nameof(Order.Id)])]
    public List<Order> Orders { get; init; } = [];
}

public sealed record Order(int Id)
{
    [DeepCompare(OrderInsensitive = true, KeyMembers = [nameof(Line.Sku)])]
    public List<Line> Lines { get; init; } = [];
}

public sealed record Line(string Sku, int Qty);
