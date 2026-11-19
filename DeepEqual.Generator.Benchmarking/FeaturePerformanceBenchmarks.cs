using System.Buffers;
using BenchmarkDotNet.Attributes;
using DeepEqual.Generator.Shared;

namespace DeepEqual.Generator.Benchmarking;

[MemoryDiagnoser]
public class FeaturePerformanceBenchmarks
{
    private const int Size = 128;
    private static readonly BinaryDeltaOptions WireFormat = new() { IncludeHeader = true, StableTypeFingerprint = 1 };
    private readonly PatchInventory _dictionaryCopy = MakeInventory();
    private readonly CountsBenchModel _access = new();

    private ArrayInventory _arrayLeft = null!;
    private ArrayInventory _arrayRight = null!;
    private KeyedInventory _keyedLeft = null!;
    private KeyedInventory _keyedRight = null!;
    private UnorderedInventory _unorderedLeft = null!;
    private UnorderedInventory _unorderedRight = null!;
    private PolymorphicInventory _polyLeft = null!;
    private PolymorphicInventory _polyRight = null!;
    private CycleItem _cycleLeft = null!;
    private CycleItem _cycleRight = null!;
    private PatchInventory _before = null!;
    private PatchInventory _desired = null!;
    private DeltaDocument _delta = null!;
    private byte[] _encoded = null!;
    private DirtyQuantity _dirtyBefore = null!;
    private DirtyQuantity _dirtyDesired = null!;

    [GlobalSetup]
    public void Setup()
    {
        _arrayLeft = new() { Items = MakeItems() };
        _arrayRight = new() { Items = MakeItems() };
        _arrayRight.Items[^1] = _arrayRight.Items[^1] with { Quantity = -1 };
        _keyedLeft = new() { Items = MakeItems().ToList() };
        _keyedRight = new() { Items = MakeItems().Reverse().ToList() };
        var quantities = Enumerable.Range(0, Size).Select(value => value % 17).ToArray();
        _unorderedLeft = new() { Quantities = quantities };
        _unorderedRight = new() { Quantities = quantities.Reverse().ToArray() };
        _polyLeft = new() { Items = MakeItems().Cast<IInventoryItem>().ToList() };
        _polyRight = new() { Items = MakeItems().Cast<IInventoryItem>().ToList() };
        _cycleLeft = MakeCycle();
        _cycleRight = MakeCycle();
        _before = MakeInventory();
        _desired = MakeInventory();
        for (var index = 0; index < 8; index++)
            _desired.Locations[$"LOC-{index}"] += 2;
        _desired.Quantity++;

        _delta = ComputeDelta();
        var buffer = new ArrayBufferWriter<byte>();
        BinaryDeltaCodec.Write(_delta, buffer, WireFormat);
        _encoded = buffer.WrittenSpan.ToArray();
        _dirtyBefore = new() { Quantity = 1 };
        _dirtyDesired = new() { Quantity = 3 };

        var difference = ExplainArrayMismatch();
        if (ArrayEquality() || !KeyedEquality() || !UnorderedEquality() || !PolymorphicEquality() || !CycleEquality() ||
            difference is not { Path: "$.Items[127].Quantity", Expected: 127, Actual: -1 })
            throw new InvalidOperationException("An equality fixture returned an unexpected result.");

        if (!DictionaryEquality() || ComputeDiff().IsEmpty || _delta.IsEmpty ||
            !_desired.AreDeepEqual(ApplyDeltaToFreshModel()) ||
            !_desired.AreDeepEqual(MakeInventory().ApplyDeepDelta(DecodeDelta())) ||
            _dirtyBefore.ApplyDeepDelta(ComputeDirtyDelta())?.Quantity != 3)
            throw new InvalidOperationException("A diff or delta fixture failed its round-trip check.");

        _dirtyBefore = new() { Quantity = 1 };
        RecordTrackedWrite();
        if (!_access.__DumpAccessJson(reset: true).Contains("\"count\"", StringComparison.Ordinal))
            throw new InvalidOperationException("Access tracking did not record the write.");
    }

    [Benchmark] public bool ArrayEquality() => _arrayLeft.AreDeepEqual(_arrayRight);
    [Benchmark] public bool KeyedEquality() => _keyedLeft.AreDeepEqual(_keyedRight);
    [Benchmark] public bool UnorderedEquality() => _unorderedLeft.AreDeepEqual(_unorderedRight);
    [Benchmark] public bool PolymorphicEquality() => _polyLeft.AreDeepEqual(_polyRight);
    [Benchmark] public bool CycleEquality() => _cycleLeft.AreDeepEqual(_cycleRight);
    [Benchmark] public DeepMismatch? ExplainArrayMismatch() => _arrayLeft.GetDeepMismatch(_arrayRight);
    [Benchmark] public bool DictionaryEquality() => _before.AreDeepEqual(_dictionaryCopy);
    [Benchmark] public Diff<PatchInventory> ComputeDiff() => _before.GetDeepDiff(_desired).diff;
    [Benchmark] public DeltaDocument ComputeDelta() => _before.ComputeDeepDelta(_desired);

    // Include constructing the mutable target so every operation applies to the original state.
    [Benchmark] public PatchInventory? ApplyDeltaToFreshModel() => MakeInventory().ApplyDeepDelta(_delta);

    [Benchmark]
    public int EncodeDelta()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BinaryDeltaCodec.Write(_delta, buffer, WireFormat);
        return buffer.WrittenCount;
    }

    [Benchmark] public DeltaDocument DecodeDelta() => BinaryDeltaCodec.Read(_encoded, WireFormat);

    [Benchmark]
    public DeltaDocument ComputeDirtyDelta()
    {
        _dirtyDesired.MarkQuantityDirty();
        return _dirtyBefore.ComputeDeepDelta(_dirtyDesired);
    }

    [Benchmark] public int RecordTrackedWrite() => ++_access.Value;

    private static InventoryItem[] MakeItems() =>
        Enumerable.Range(0, Size).Select(index => new InventoryItem($"SKU-{index}", index)).ToArray();

    private static CycleItem MakeCycle()
    {
        var first = new CycleItem { Value = 1 };
        first.Next = new CycleItem { Value = 2, Next = first };
        return first;
    }

    private static PatchInventory MakeInventory() => new()
    {
        Quantity = Size,
        Locations = Enumerable.Range(0, Size).ToDictionary(index => $"LOC-{index}", index => index)
    };
}

public interface IInventoryItem { }

[DeepComparable]
public sealed record InventoryItem(string Sku, int Quantity) : IInventoryItem;

[DeepComparable]
public sealed class ArrayInventory
{
    public InventoryItem[] Items { get; init; } = [];
}

[DeepComparable]
public sealed class KeyedInventory
{
    [DeepCompare(KeyMembers = [nameof(InventoryItem.Sku)])]
    public List<InventoryItem> Items { get; init; } = [];
}

[DeepComparable]
public sealed class UnorderedInventory
{
    [DeepCompare(OrderInsensitive = true)]
    public int[] Quantities { get; init; } = [];
}

[DeepComparable]
public sealed class PolymorphicInventory
{
    public List<IInventoryItem> Items { get; init; } = [];
}

[DeepComparable]
public sealed class CycleItem
{
    public int Value { get; init; }
    public CycleItem? Next { get; set; }
}

[DeepComparable(GenerateDiff = true, GenerateDelta = true)]
public sealed class PatchInventory
{
    public int Quantity { get; set; }
    public Dictionary<string, int> Locations { get; set; } = new();
}

[DeepComparable(GenerateDelta = true)]
[DeltaTrack]
public sealed partial class DirtyQuantity
{
    public int Quantity { get; set; }

    public void MarkQuantityDirty() => __MarkDirty(__Bit_Quantity);
}
