using System.Buffers;
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeepEqual.Generator.Shared;

[assembly: ExternalDeepComparable(typeof(DeepEqual.WorkflowExternalDto), GenerateDiff = true, GenerateDelta = true)]
[assembly: ExternalDeepCompare(typeof(DeepEqual.WorkflowExternalDto), nameof(DeepEqual.WorkflowExternalDto.ImportedAt),
    Kind = CompareKind.Skip)]

namespace DeepEqual;

public sealed class InventoryWorkflowTests
{
    [Fact]
    public void ReportUsesKeysAndContainsAllRelevantChanges()
    {
        var before = new WorkflowInventory { Name = "Old", Lines = [new("A", 1), new("B", 2)] };
        var after = new WorkflowInventory { Name = "New", Lines = [new("C", 4), new("A", 3)] };
        var report = before.DescribeDeepChanges(after);
        Assert.Equal(4, report.Changes.Count);
        Assert.Contains(report.Changes, change => change is { Path: "$.Lines[\"A\"].Quantity", Expected: 1, Actual: 3 });
        Assert.Contains(report.Changes, change => change is { Path: "$.Lines[\"B\"]", Kind: DeepMismatchKind.Removed });
        Assert.Contains(report.Changes, change => change is { Path: "$.Lines[\"C\"]", Kind: DeepMismatchKind.Added });
        Assert.Contains(report.Changes, change => change.Path == "$.Name");
        Assert.False(report.IsTruncated);
        Assert.False(after.DescribeDeepChanges(after).HasChanges);
    }

    [Fact]
    public void ReportLimitsAndRedactionApplyToTextAndJson()
    {
        var before = new WorkflowInventory { Name = "hidden value", Locations = new() { ["A"] = 1 } };
        var after = new WorkflowInventory { Name = "new secret", Locations = new() { ["A"] = 2 } };
        Assert.True(before.DescribeDeepChanges(after, maxChanges: 1).IsTruncated);
        Assert.False(before.DescribeDeepChanges(after, maxChanges: 2).IsTruncated);
        var format = new ChangeFormatOptions { RedactPath = path => path == "$.Name" };
        var exception = Assert.Throws<DeepComparisonException>(() => before.AssertDeepEqual(after, format: format));
        Assert.DoesNotContain("hidden", exception.Message);
        Assert.DoesNotContain("$.Name", exception.Message);
        Assert.Contains("$.Locations[\"A\"]", exception.Message);
        using var json = JsonDocument.Parse(exception.Report.ToJson(format));
        Assert.Equal(2, json.RootElement.GetProperty("changes").GetArrayLength());
        Assert.DoesNotContain("new secret", json.RootElement.GetRawText());
    }

    [Fact]
    public void ModelExplanationAndComparisonProfileReuseTheGeneratedRules()
    {
        var before = new WorkflowInventory { Name = "north", Timestamp = 1 };
        var after = new WorkflowInventory { Name = "NORTH", Timestamp = 2 };
        var model = before.ExplainDeepModel();
        var lines = Assert.Single(model.Members, member => member.Name == nameof(WorkflowInventory.Lines));
        Assert.Equal(DeepCollectionMode.Keyed, lines.Collection);
        Assert.Equal(nameof(WorkflowLine.Sku), lines.KeyMembers);
        Assert.Equal(DeepMemberMode.Skipped,
            Assert.Single(model.Members, member => member.Name == nameof(WorkflowInventory.Timestamp)).Mode);
        var profile = new ComparisonProfile("inventory", new() { StringComparison = StringComparison.OrdinalIgnoreCase });
        before.AssertDeepEqual(after, profile.Options);
        Assert.True(before.ComputeDeepDelta(after, profile.CreateContext()).IsEmpty);
        Assert.False(before.DescribeDeepChanges(after, profile.Options).HasChanges);
    }

    [Fact]
    public void RuntimeFallbackIsReportedEvenIfItReturnsEqual()
    {
        var before = new WorkflowDynamic { Item = new UnregisteredValue(1) };
        var after = new WorkflowDynamic { Item = new UnregisteredValue(1) };
        var report = before.DescribeDeepChanges(after);
        Assert.False(report.HasChanges);
        Assert.Equal("$.Item", Assert.Single(report.RuntimeFallbacks).Path);
    }

    [Fact]
    public void GeneratedSyncAppliesToACopyAndRecoversFromAMissingRevision()
    {
        var before = new WorkflowInventory { Name = "north", Locations = new() { ["A"] = 2, ["B"] = 3 } };
        var after = new WorkflowInventory { Name = "north", Locations = new() { ["A"] = 4, ["C"] = 5 } };
        var sender = before.CreateDeepSync(InventoryJson.Default.WorkflowInventory);
        var receiver = before.CreateDeepSync(InventoryJson.Default.WorkflowInventory);
        var update = sender.CreateUpdate(before, after, 17, 18);
        Assert.False(update.IsSnapshot);
        Assert.Equal(sender.SchemaFingerprint, receiver.SchemaFingerprint);
        var dryRun = receiver.Preview(before, 17, update.Bytes);
        Assert.True(dryRun.Accepted);
        Assert.Equal(2, before.Locations["A"]);
        after.AssertDeepEqual(dryRun.Value);
        var result = receiver.Apply(before, 17, update.Bytes);
        Assert.Equal(18, result.Revision);
        after.AssertDeepEqual(result.Value);
        Assert.Equal(DeepSyncStatus.AlreadyApplied, receiver.Apply(result.Value, 18, update.Bytes).Status);
        var stale = receiver.Apply(before, 16, update.Bytes);
        Assert.Equal(DeepSyncStatus.StaleBaseline, stale.Status);
        Assert.Same(before, stale.Value);
        var fallback = sender.CreateSnapshot(after, 18);
        after.AssertDeepEqual(receiver.Apply(before, 16, fallback.Bytes).Value);
        var inspection = receiver.Inspect(update.Bytes);
        Assert.Contains(inspection.Operations.Changes, entry => entry.Path == "$.Locations[\"A\"]");
        Assert.Equal(17, inspection.BaselineRevision);
    }

    [Fact]
    public void RejectsUnknownOperationsAndMalformedBytesBeforeChangingReceiver()
    {
        var before = new WorkflowInventory { Name = "before", Locations = new() { ["A"] = 1 } };
        var after = new WorkflowInventory { Name = "after", Locations = new() { ["A"] = 2 } };
        var sync = before.CreateDeepSync(InventoryJson.Default.WorkflowInventory);
        var valid = sync.CreateUpdate(before, after, 1, 2);
        Assert.False(valid.IsSnapshot);
        var operations = before.ComputeDeepDelta(after);
        operations.Ops.Add(new DeltaOp(int.MaxValue, DeltaKind.SetMember, -1, null, "invalid", null));
        var bytes = new ArrayBufferWriter<byte>();
        BinaryDeltaCodec.Write(operations, bytes);
        var invalid = new byte[29 + bytes.WrittenCount];
        valid.Bytes.AsSpan(0, 29).CopyTo(invalid);
        bytes.WrittenSpan.CopyTo(invalid.AsSpan(29));
        var result = sync.Apply(before, 1, invalid);
        Assert.Equal(DeepSyncStatus.InvalidOperation, result.Status);
        Assert.Same(before, result.Value);
        Assert.Equal("before", before.Name);
        Assert.Equal(1, before.Locations["A"]);
        Assert.Equal(DeepSyncStatus.InvalidOperation, sync.Apply(before, 1, valid.Bytes.AsSpan(0, 30)).Status);
        BinaryPrimitives.WriteUInt64LittleEndian(invalid.AsSpan(4), sync.SchemaFingerprint ^ 1);
        Assert.Equal(DeepSyncStatus.SchemaMismatch, sync.Apply(before, 1, invalid).Status);
    }

    [Fact]
    public void UnsupportedTypedValueHasAPathAndRegisteredValuesTravelAsPatches()
    {
        var before = new WorkflowInventory();
        var after = new WorkflowInventory { Lines = [new("A", 3)] };
        var plain = before.CreateDeepSync(InventoryJson.Default.WorkflowInventory);
        Assert.StartsWith("$.Lines", plain.CheckBinaryPatch(before.ComputeDeepDelta(after))?.Path);
        Assert.True(plain.CreateUpdate(before, after, 5, 6).IsSnapshot);
        var options = new DeepSyncOptions
        {
            ValueTypes = new(new BinaryDeltaType(1, InventoryJson.Default.WorkflowLine),
                new BinaryDeltaType(2, InventoryJson.Default.ListWorkflowLine))
        };
        var typed = before.CreateDeepSync(InventoryJson.Default.WorkflowInventory, options);
        var update = typed.CreateUpdate(before, after, 5, 6);
        Assert.False(update.IsSnapshot);
        after.AssertDeepEqual(typed.Apply(before, 5, update.Bytes).Value);
        Assert.Equal(DeepSyncStatus.SchemaMismatch, plain.Apply(before, 5, update.Bytes).Status);
    }

    [Fact]
    public void DifferentDictionaryComparersCanRequireAWholeMemberReplacement()
    {
        var before = new WorkflowInventory { Locations = new(StringComparer.OrdinalIgnoreCase) { ["a"] = 1 } };
        var after = new WorkflowInventory { Locations = new() { ["a"] = 1, ["A"] = 2 } };
        Assert.Equal(DeltaKind.SetMember, Assert.Single(before.ComputeDeepDelta(after).Operations).Kind);
        after.AssertDeepEqual(before.ApplyDeepDelta(before.ComputeDeepDelta(after)));
    }

    [Fact]
    public void OrderedKeysProduceMoveReportsAndAnOrderPreservingPatch()
    {
        var before = new OrderedWorkflow { Lines = [new("A", 1), new("B", 2)] };
        var after = new OrderedWorkflow { Lines = [new("B", 4), new("A", 1)] };
        Assert.False(before.AreDeepEqual(after));
        Assert.Contains(before.DescribeDeepChanges(after).Changes,
            change => change is { Kind: DeepMismatchKind.Moved, Path: "$.Lines[\"A\"]", Expected: 0, Actual: 1 });
        after.AssertDeepEqual(before.ApplyDeepDelta(before.ComputeDeepDelta(after)));
    }

    [Fact]
    public void KeyedMembershipChangesUseTheSurvivingItemsActualPositions()
    {
        var before = new WorkflowInventory { Lines = [new("A", 1), new("B", 2), new("C", 3)] };
        var after = new WorkflowInventory { Lines = [new("C", 3), new("B", 5), new("D", 6)] };
        var patch = before.ComputeDeepDelta(after);
        Assert.Contains(patch.Operations, operation => operation.Kind == DeltaKind.SeqRemoveAt);
        Assert.Contains(patch.Operations, operation => operation.Kind == DeltaKind.SeqAddAt);
        after.AssertDeepEqual(before.ApplyDeepDelta(patch));
    }

    [Fact]
    public void EqualDuplicateKeyBucketsDoNotEmitPatchesOrReports()
    {
        var before = new WorkflowInventory { Lines = [new("A", 1), new("A", 2)] };
        var after = new WorkflowInventory { Lines = [new("A", 2), new("A", 1)] };
        after.AssertDeepEqual(before);
        Assert.True(before.ComputeDeepDelta(after).IsEmpty);
        Assert.True(before.GetDeepDiff(after).diff.IsEmpty);
        Assert.False(before.DescribeDeepChanges(after).HasChanges);
    }

    [Fact]
    public void KeyedPatchRoundTripsManyMembershipOrdersAndUpdates()
    {
        var random = new Random(9814);
        var ids = Enumerable.Range(0, 10).Select(i => $"SKU-{i}").ToArray();
        for (var test = 0; test < 100; test++)
        {
            var before = MakeSnapshot();
            var after = MakeSnapshot();
            var reportsChanges = before.DescribeDeepChanges(after).HasChanges;
            var delta = before.ComputeDeepDelta(after);
            Assert.Equal(!before.AreDeepEqual(after), reportsChanges);
            Assert.Equal(!reportsChanges, delta.IsEmpty);
            after.AssertDeepEqual(before.ApplyDeepDelta(delta));
        }

        WorkflowInventory MakeSnapshot() => new()
        {
            Lines = ids.Where(_ => random.Next(3) != 0).OrderBy(_ => random.Next())
                .Select(id => new WorkflowLine(id, random.Next(3))).ToList()
        };
    }

    [Fact]
    public void FailedSetterCannotPublishEarlierOperationsFromTheSamePacket()
    {
        var before = new GuardedWorkflow { First = 1, Second = 2 };
        var after = new GuardedWorkflow { First = 3, Second = 4 };
        var sync = before.CreateDeepSync(InventoryJson.Default.GuardedWorkflow);
        var valid = sync.CreateUpdate(before, after, 10, 11);
        var delta = before.ComputeDeepDelta(after);
        var set = delta.Ops.FindIndex(operation => Equals(operation.Value, 4));
        var original = delta.Ops[set];
        delta.Ops[set] = new DeltaOp(original.MemberIndex, original.Kind, original.Index, original.Key, -1, original.Nested);
        var buffer = new ArrayBufferWriter<byte>();
        BinaryDeltaCodec.Write(delta, buffer);
        var corrupted = valid.Bytes[..29].Concat(buffer.WrittenSpan.ToArray()).ToArray();
        var rejected = sync.Apply(before, 10, corrupted);
        Assert.Equal(DeepSyncStatus.InvalidOperation, rejected.Status);
        Assert.Same(before, rejected.Value);
        Assert.Equal(1, before.First);
        Assert.Equal(2, before.Second);
    }

    [Fact]
    public void GenericRootReplacementUsesTheSameRulesAsItsReports()
    {
        var before = new WorkflowBox<string>("a");
        var after = new WorkflowBox<string>("b");
        Assert.True(before.GetDeepDiff(after).diff.IsReplacement);
        Assert.True(before.DescribeDeepChanges(after).HasChanges);
        after.AssertDeepEqual(before.ApplyDeepDelta(before.ComputeDeepDelta(after)));
    }

    [Fact]
    public void ExternalRulesApplyToDiffAndPatchGeneration()
    {
        var before = new WorkflowExternalDto { Quantity = 1, ImportedAt = 17 };
        var after = new WorkflowExternalDto { Quantity = 1, ImportedAt = 18 };
        before.AssertDeepEqual(after);
        Assert.True(before.GetDeepDiff(after).diff.IsEmpty);
        Assert.True(before.ComputeDeepDelta(after).IsEmpty);
        after.Quantity = 2;
        Assert.True(before.GetDeepDiff(after).diff.IsReplacement);
        after.AssertDeepEqual(before.ApplyDeepDelta(before.ComputeDeepDelta(after)));
    }

    [Fact]
    public void UntypedReadOnlyNestedMapUsesAnApplicableReplacement()
    {
        var before = new WorkflowMaps { Maps = new() { ["site"] = ReadOnlyMap(1) } };
        var after = new WorkflowMaps { Maps = new() { ["site"] = ReadOnlyMap(2) } };
        var delta = before.ComputeDeepDelta(after);
        Assert.Equal(DeltaKind.DictSet, Assert.Single(delta.Operations).Kind);
        after.AssertDeepEqual(before.ApplyDeepDelta(delta));

        static ReadOnlyDictionary<string, int> ReadOnlyMap(int quantity) =>
            new(new Dictionary<string, int> { ["SKU-9"] = quantity });
    }

    [Fact]
    public void CustomComparersUnorderedCollectionsAndStrictDatesUseTheSameRules()
    {
        var before = new WorkflowRules
        {
            Label = " north ", Scores = [1, 2, 2], LocalTime = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Local)
        };
        var equivalent = new WorkflowRules { Label = "NORTH", Scores = [2, 2, 1], LocalTime = before.LocalTime };
        equivalent.AssertDeepEqual(before);
        Assert.True(before.GetDeepDiff(equivalent).diff.IsEmpty);
        Assert.True(before.ComputeDeepDelta(equivalent).IsEmpty);
        equivalent.LocalTime = DateTime.SpecifyKind(equivalent.LocalTime, DateTimeKind.Utc);
        Assert.False(before.AreDeepEqual(equivalent));
        equivalent.AssertDeepEqual(before.ApplyDeepDelta(before.ComputeDeepDelta(equivalent)));
    }

    public sealed record UnregisteredValue(int Number);
}

[DeepComparable(GenerateDiff = true, GenerateDelta = true)]
public sealed class WorkflowInventory
{
    public string Name { get; set; } = "";
    [DeepCompare(KeyMembers = [nameof(WorkflowLine.Sku)])]
    public List<WorkflowLine> Lines { get; set; } = [];
    [DeepCompare(Kind = CompareKind.Skip)]
    public long Timestamp { get; set; }
    public Dictionary<string, int> Locations { get; set; } = new();
}

public sealed record WorkflowLine(string Sku, int Quantity);

[DeepComparable]
public sealed class WorkflowDynamic
{
    public object? Item { get; set; }
}

[DeepComparable(GenerateDelta = true)]
public sealed class OrderedWorkflow
{
    [DeepCompare(OrderInsensitive = false, KeyMembers = [nameof(WorkflowLine.Sku)])]
    public List<WorkflowLine> Lines { get; set; } = [];
}

[DeepComparable(GenerateDiff = true, GenerateDelta = true)]
public sealed record WorkflowBox<T>(T Content);

public sealed class WorkflowExternalDto
{
    public int Quantity { get; set; }
    public long ImportedAt { get; set; }
}

[DeepComparable(GenerateDelta = true)]
public sealed class WorkflowMaps
{
    public Dictionary<string, object> Maps { get; set; } = new();
}

[DeepComparable(GenerateDiff = true, GenerateDelta = true)]
public sealed class WorkflowRules
{
    [DeepCompare(ComparerType = typeof(NormalizedLabelComparer))]
    public string Label { get; set; } = "";
    [DeepCompare(OrderInsensitive = true)]
    public int[] Scores { get; set; } = [];
    public DateTime LocalTime { get; set; }
}

public sealed class NormalizedLabelComparer : IEqualityComparer<string>
{
    public bool Equals(string? x, string? y) => StringComparer.OrdinalIgnoreCase.Equals(x?.Trim(), y?.Trim());
    public int GetHashCode(string obj) => StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Trim());
}

[DeepComparable(GenerateDelta = true)]
public sealed class GuardedWorkflow
{
    private int _second;
    public int First { get; set; }
    public int Second
    {
        get => _second;
        set => _second = value >= 0 ? value : throw new InvalidOperationException("Quantity cannot be negative.");
    }
}

[JsonSerializable(typeof(WorkflowInventory))]
[JsonSerializable(typeof(WorkflowLine))]
[JsonSerializable(typeof(List<WorkflowLine>))]
[JsonSerializable(typeof(GuardedWorkflow))]
internal partial class InventoryJson : JsonSerializerContext;
