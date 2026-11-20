using System.Text.Json.Serialization;
using DeepEqual;
using DeepEqual.Generator.Shared;

var original = new Inventory
{
    Warehouse = "North",
    Lines = [new("SKU-9", 2), new("SKU-11", 1)],
    Locations = new() { ["A"] = 2, ["B"] = 1 }
};
var desired = new Inventory
{
    Warehouse = "NORTH",
    Lines = [new("SKU-9", 3), new("SKU-12", 1)],
    Locations = new() { ["A"] = 3, ["C"] = 1 }
};
for (var location = 0; location < 64; location++)
{
    original.Locations[$"location-{location}"] = location;
    desired.Locations[$"location-{location}"] = location;
}
var profile = new ComparisonProfile("Inventory", new() { StringComparison = StringComparison.OrdinalIgnoreCase });
var options = new DeepSyncOptions
{
    Profile = profile,
    ValueTypes = new(new BinaryDeltaType(1, InventoryJson.Default.InventoryLine),
        new BinaryDeltaType(2, InventoryJson.Default.ListInventoryLine))
};
var sender = original.CreateDeepSync(InventoryJson.Default.Inventory, options);
var receiver = original.CreateDeepSync(InventoryJson.Default.Inventory, options);

if (args is ["inspect", var capturedPath])
{
    var captured = receiver.Inspect(File.ReadAllBytes(capturedPath));
    Console.WriteLine($"Revision {captured.BaselineRevision} → {captured.Revision}");
    Console.WriteLine(captured.IsSnapshot ? "Full snapshot" : captured.Operations.ToJson());
    return;
}

if (args is ["apply", var patchPath])
{
    var restored = receiver.Apply(original, 17, File.ReadAllBytes(patchPath));
    if (restored.Status != DeepSyncStatus.Applied)
        throw new InvalidOperationException(restored.Issue?.Message ?? "Expected a new inventory revision.");
    desired.AssertDeepEqual(restored.Value, profile.Options);
    Console.WriteLine($"Verified captured patch at revision {restored.Revision}.");
    return;
}

Console.WriteLine(original.DescribeDeepChanges(desired, profile.Options).ToText());
var update = sender.CreateUpdate(original, desired, baselineRevision: 17, revision: 18);
var preview = receiver.Preview(original, 17, update.Bytes);
var applied = receiver.Apply(original, 17, update.Bytes);
if (!preview.Accepted || !applied.Accepted || applied.Revision != 18 || original.Lines[0].Quantity != 2)
    throw new InvalidOperationException("The checked patch did not stage an independent revision.");
desired.AssertDeepEqual(applied.Value, profile.Options);

var stale = receiver.Apply(original, 16, update.Bytes);
if (stale.Status != DeepSyncStatus.StaleBaseline)
    throw new InvalidOperationException("The sender should have produced a patch for revision 17.");
var snapshot = sender.CreateSnapshot(desired, 18);
desired.AssertDeepEqual(receiver.Apply(original, 16, snapshot.Bytes).Value, profile.Options);

Console.WriteLine($"Applied revision {applied.Revision}. The stale receiver recovered from a full snapshot.");
Console.WriteLine($"Patch {update.Bytes.Length} bytes, full snapshot {snapshot.Bytes.Length} bytes.");
if (args is ["write", var outputPath])
    File.WriteAllBytes(outputPath, update.Bytes);

[DeepComparable(GenerateDiff = true, GenerateDelta = true)]
public sealed class Inventory
{
    public string Warehouse { get; set; } = "";

    [DeepCompare(KeyMembers = [nameof(InventoryLine.Sku)])]
    public List<InventoryLine> Lines { get; set; } = [];

    public Dictionary<string, int> Locations { get; set; } = new();
}

public sealed record InventoryLine(string Sku, int Quantity);

[JsonSerializable(typeof(Inventory))]
[JsonSerializable(typeof(InventoryLine))]
[JsonSerializable(typeof(List<InventoryLine>))]
internal partial class InventoryJson : JsonSerializerContext;
