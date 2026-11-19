using System.Buffers;
using DeepEqual;
using DeepEqual.Generator.Shared;

var original = new Stock { Quantity = 2, Locations = new() { ["A"] = 2 } };
var desired = new Stock { Quantity = 3, Locations = new() { ["A"] = 3, ["B"] = 1 } };
var (hasChanges, _) = original.GetDeepDiff(desired);
var document = original.ComputeDeepDelta(desired);
var profile = new BinaryDeltaOptions { IncludeHeader = true, StableTypeFingerprint = 1 };
var bytes = new ArrayBufferWriter<byte>();
document.ToBinary(bytes, profile);
var patch = DeltaDocumentBinaryExtensions.FromBinary(bytes.WrittenSpan, profile);
var actual = original.ApplyDeepDelta(patch);

if (!hasChanges || patch.IsEmpty || !desired.AreDeepEqual(actual))
    throw new InvalidOperationException("The binary delta did not reproduce the desired stock.");

Console.WriteLine($"Patched quantity to {actual!.Quantity} across {actual.Locations.Count} locations.");

[DeepComparable(GenerateDiff = true, GenerateDelta = true)]
public sealed class Stock
{
    public int Quantity { get; set; }
    public Dictionary<string, int> Locations { get; set; } = new();
}
