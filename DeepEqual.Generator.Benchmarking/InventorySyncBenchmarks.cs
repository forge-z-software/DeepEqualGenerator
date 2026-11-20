using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using DeepEqual.Generator.Shared;

namespace DeepEqual.Generator.Benchmarking;

[MemoryDiagnoser]
public class InventorySyncBenchmarks
{
    [Params(128, 4096)]
    public int Entries { get; set; }

    private SyncBenchmarkModel _before = null!;
    private SyncBenchmarkModel _after = null!;
    private DeepSync<SyncBenchmarkModel> _sync = null!;
    private DeepSyncMessage _patch = null!;
    private DeepSyncMessage _snapshot = null!;

    [GlobalSetup]
    public void Setup()
    {
        _before = MakeInventory();
        _after = MakeInventory();
        for (var i = 0; i < 8; i++) _after.Stock[$"warehouse/{i}"] += 1;
        _sync = _before.CreateDeepSync(SyncBenchmarkJson.Default.SyncBenchmarkModel);
        _patch = CreatePatch();
        _snapshot = CreateSnapshot();
        var fromPatch = ReceivePatch();
        var fromSnapshot = ReceiveSnapshot();
        if (_patch.IsSnapshot || !_snapshot.IsSnapshot || !fromPatch.Accepted || !fromSnapshot.Accepted ||
            !_after.AreDeepEqual(fromPatch.Value) || !_after.AreDeepEqual(fromSnapshot.Value) ||
            _before.Stock["warehouse/0"] != 0 || Report().Changes.Count != 8)
            throw new InvalidOperationException("The sync benchmark fixtures failed validation.");
        Console.WriteLine($"{Entries} entries: patch = {_patch.Bytes.Length} bytes, snapshot = {_snapshot.Bytes.Length} bytes.");
    }

    [Benchmark] public DeepChangeReport Report() => _before.DescribeDeepChanges(_after);
    [Benchmark] public DeepSyncMessage CreatePatch() => _sync.CreateUpdate(_before, _after, 1, 2);
    [Benchmark] public DeepSyncResult<SyncBenchmarkModel> ReceivePatch() => _sync.Apply(_before, 1, _patch.Bytes);
    [Benchmark] public DeepSyncMessage CreateSnapshot() => _sync.CreateSnapshot(_after, 2);
    [Benchmark] public DeepSyncResult<SyncBenchmarkModel> ReceiveSnapshot() => _sync.Apply(_before, 1, _snapshot.Bytes);

    private SyncBenchmarkModel MakeInventory() => new()
    {
        Stock = Enumerable.Range(0, Entries).ToDictionary(i => $"warehouse/{i}", i => i)
    };
}

[DeepComparable(GenerateDelta = true)]
public sealed class SyncBenchmarkModel
{
    public Dictionary<string, int> Stock { get; set; } = new();
}

[JsonSerializable(typeof(SyncBenchmarkModel))]
internal sealed partial class SyncBenchmarkJson : JsonSerializerContext;
