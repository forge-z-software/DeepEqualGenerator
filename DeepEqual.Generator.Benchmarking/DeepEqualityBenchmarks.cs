using BenchmarkDotNet.Attributes;
using DeepEqual.Generator.Shared;

namespace DeepEqual.Generator.Benchmarking;

[MemoryDiagnoser]
public class DeepEqualityBenchmarks
{
    private BenchmarkOrder _expected = null!;
    private BenchmarkOrder _actual = null!;

    [Params(-1, 0, 127)]
    public int ChangedIndex { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _expected = MakeOrder();
        _actual = MakeOrder();
        if (ChangedIndex >= 0)
            _actual.Lines[ChangedIndex] = _actual.Lines[ChangedIndex] with { Qty = 3 };

        var expectedResult = ChangedIndex < 0;
        var mismatch = Explain();
        if (Handwritten() != expectedResult || Generated() != expectedResult ||
            (expectedResult ? mismatch is not null :
                mismatch is not { Expected: 2, Actual: 3 } || mismatch.Path != $"$.Lines[{ChangedIndex}].Qty"))
            throw new InvalidOperationException("A benchmark comparison returned an unexpected result.");
    }

    private static BenchmarkOrder MakeOrder() => new()
    {
        Metadata = new() { ["status"] = "ready", ["account"] = "demo" },
        Lines = Enumerable.Range(0, 128).Select(i => new BenchmarkLine($"SKU-{i}", 2)).ToList()
    };

    [Benchmark(Baseline = true)]
    public bool Handwritten()
    {
        if (_expected.Metadata.Count != _actual.Metadata.Count || _expected.Lines.Count != _actual.Lines.Count)
            return false;
        foreach (var (key, value) in _expected.Metadata)
            if (!_actual.Metadata.TryGetValue(key, out var other) || value != other)
                return false;

        for (var i = 0; i < _expected.Lines.Count; i++)
        {
            var expected = _expected.Lines[i];
            var actual = _actual.Lines[i];
            if (expected.Qty != actual.Qty || expected.Sku != actual.Sku)
                return false;
        }

        return true;
    }

    [Benchmark]
    public bool Generated() => _expected.AreDeepEqual(_actual);

    [Benchmark]
    public DeepMismatch? Explain() => _expected.GetDeepMismatch(_actual);
}

[DeepComparable]
public sealed class BenchmarkOrder
{
    public Dictionary<string, string> Metadata { get; init; } = new();
    public List<BenchmarkLine> Lines { get; init; } = [];
}

public sealed record BenchmarkLine(string Sku, int Qty);
