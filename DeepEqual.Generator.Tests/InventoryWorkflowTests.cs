using System.Buffers;
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeepEqual.Generator.Shared;

[assembly: ExternalDeepComparable(typeof(DeepEqual.WorkflowExternalDto), GenerateDiff = true, GenerateDelta = true)]
[assembly: ExternalDeepCompare(typeof(DeepEqual.WorkflowExternalDto), nameof(DeepEqual.WorkflowExternalDto.ImportedAt),
    Kind = CompareKind.Skip)]

namespace DeepEqual;

public sealed class InventoryWorkflowTests
{
    private const int SyncHeaderSize = 93;
    private const int BaselineStateOffset = 29;
    private const int ResultStateOffset = 61;

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
        var invalid = valid.Bytes[..SyncHeaderSize].Concat(bytes.WrittenSpan.ToArray()).ToArray();
        var result = sync.Apply(before, 1, invalid);
        Assert.Equal(DeepSyncStatus.InvalidOperation, result.Status);
        Assert.Same(before, result.Value);
        Assert.Equal("before", before.Name);
        Assert.Equal(1, before.Locations["A"]);
        Assert.Equal(DeepSyncStatus.InvalidOperation, sync.Apply(before, 1, valid.Bytes.AsSpan(0, SyncHeaderSize + 1)).Status);
        BinaryPrimitives.WriteUInt64LittleEndian(invalid.AsSpan(4), sync.SchemaFingerprint ^ 1);
        Assert.Equal(DeepSyncStatus.SchemaMismatch, sync.Apply(before, 1, invalid).Status);
    }

    [Fact]
    public void CorrectRevisionWithDriftedStateIsRejectedWithoutMutation()
    {
        var before = new WorkflowInventory { Name = "north", Locations = new() { ["A"] = 2, ["B"] = 5 } };
        var after = new WorkflowInventory { Name = "north", Locations = new() { ["A"] = 3, ["B"] = 5 } };
        var receiver = new WorkflowInventory { Name = "south", Locations = new() { ["A"] = 2, ["B"] = 5 } };
        var sync = before.CreateDeepSync(InventoryJson.Default.WorkflowInventory);
        var update = sync.CreateUpdate(before, after, 17, 18);
        Assert.False(update.IsSnapshot);

        var dryRun = sync.Preview(receiver, 17, update.Bytes);
        var applied = sync.Apply(receiver, 17, update.Bytes);
        Assert.Equal(DeepSyncStatus.StateMismatch, dryRun.Status);
        Assert.Equal(dryRun, applied);
        Assert.Same(receiver, applied.Value);
        Assert.Equal(17, applied.Revision);
        Assert.Equal("south", receiver.Name);
        Assert.Equal(2, receiver.Locations["A"]);
        after.AssertDeepEqual(sync.Apply(receiver, 17, sync.CreateSnapshot(after, 18).Bytes).Value);
    }

    [Fact]
    public void ValidOperationsWithTheWrongResultAreRejectedAfterStaging()
    {
        var before = new WorkflowInventory { Name = "north", Locations = new() { ["A"] = 2 } };
        var after = new WorkflowInventory { Name = "north", Locations = new() { ["A"] = 3 } };
        var sync = before.CreateDeepSync(InventoryJson.Default.WorkflowInventory);
        var update = sync.CreateUpdate(before, after, 7, 8);
        var alternate = sync.CreateUpdate(before, new WorkflowInventory { Name = "north", Locations = new() { ["A"] = 9 } }, 7, 8);
        var edited = update.Bytes[..SyncHeaderSize].Concat(alternate.Bytes[SyncHeaderSize..]).ToArray();
        var result = sync.Apply(before, 7, edited);

        Assert.Equal(DeepSyncStatus.StateMismatch, result.Status);
        Assert.Contains("staged", result.Issue!.Message);
        Assert.Same(before, result.Value);
        Assert.Equal(7, result.Revision);
        Assert.Equal(2, before.Locations["A"]);
    }

    [Fact]
    public void StateVerificationAcceptsDifferentDictionaryInsertionOrder()
    {
        var before = new WorkflowInventory { Locations = new() { ["B"] = 2, ["A"] = 1 } };
        var after = new WorkflowInventory { Locations = new() { ["A"] = 4, ["B"] = 2 } };
        var copy = new WorkflowInventory { Locations = new() { ["A"] = 1, ["B"] = 2 } };
        var sync = before.CreateDeepSync(InventoryJson.Default.WorkflowInventory);

        var update = sync.CreateUpdate(before, after, 4, 5);
        Assert.False(update.IsSnapshot);
        var applied = sync.Apply(copy, 4, update.Bytes);
        Assert.Equal(DeepSyncStatus.Applied, applied.Status);
        after.AssertDeepEqual(applied.Value);
        Assert.Equal(DeepSyncStatus.AlreadyApplied, sync.Apply(after, 5, update.Bytes).Status);
        Assert.Equal(DeepSyncStatus.StateMismatch, sync.Apply(before, 5, update.Bytes).Status);
    }

    [Fact]
    public void ComparingEqualDoesNotAllowAnIncorrectSerializedResult()
    {
        var before = new WorkflowInventory { Name = "north", Lines = [new("A", 1), new("B", 2)] };
        var after = new WorkflowInventory { Name = "NORTH", Lines = [new("B", 2), new("A", 1)], Timestamp = 99 };
        var options = new DeepSyncOptions
        {
            Profile = new ComparisonProfile("inventory", new() { StringComparison = StringComparison.OrdinalIgnoreCase })
        };
        var sync = before.CreateDeepSync(InventoryJson.Default.WorkflowInventory, options);
        Assert.True(before.AreDeepEqual(after, options.Profile.Options));

        var update = sync.CreateUpdate(before, after, 4, 5);
        Assert.True(update.IsSnapshot);
        Assert.NotNull(update.SnapshotReason);
        var result = sync.Apply(before, 4, update.Bytes);
        Assert.Equal(DeepSyncStatus.Applied, result.Status);
        Assert.Equal(after.Name, result.Value!.Name);
        Assert.Equal(after.Timestamp, result.Value.Timestamp);
        Assert.Equal(after.Lines, result.Value.Lines);
    }

    [Fact]
    public void NoOpStillChecksBaselineAndResult()
    {
        var before = new WorkflowInventory { Name = "north" };
        var sync = before.CreateDeepSync(InventoryJson.Default.WorkflowInventory);
        var update = sync.CreateUpdate(before, before, 10, 11);
        Assert.False(update.IsSnapshot);
        Assert.Empty(sync.Inspect(update.Bytes).Operations.Changes);
        Assert.Same(before, sync.Apply(before, 10, update.Bytes).Value);
        Assert.Equal(DeepSyncStatus.StateMismatch, sync.Apply(new WorkflowInventory(), 10, update.Bytes).Status);
        update.Bytes[ResultStateOffset] ^= 1;
        Assert.Equal(DeepSyncStatus.StateMismatch, sync.Apply(before, 10, update.Bytes).Status);
    }

    [Fact]
    public void SnapshotsAndReplaysCheckTheResultState()
    {
        var before = new WorkflowInventory { Name = "north" };
        var after = new WorkflowInventory { Name = "south" };
        var sync = before.CreateDeepSync(InventoryJson.Default.WorkflowInventory);
        var snapshot = sync.CreateSnapshot(after, 3);
        Assert.Equal(DeepSyncStatus.AlreadyApplied, sync.Apply(after, 3, snapshot.Bytes).Status);
        Assert.Equal(DeepSyncStatus.StateMismatch, sync.Apply(before, 3, snapshot.Bytes).Status);

        var altered = snapshot.Bytes[..SyncHeaderSize].Concat(sync.CreateSnapshot(before, 3).Bytes[SyncHeaderSize..]).ToArray();
        var rejected = sync.Apply(before, 1, altered);
        Assert.Equal(DeepSyncStatus.StateMismatch, rejected.Status);
        Assert.Same(before, rejected.Value);
        Assert.Equal(1, rejected.Revision);
        Assert.Null(sync.Apply(before, 1, sync.CreateSnapshot(null, 3).Bytes).Value);
    }

    [Fact]
    public void PreviousWireVersionAndInvalidDigestFieldsAreRejected()
    {
        var before = new WorkflowInventory();
        var sync = before.CreateDeepSync(InventoryJson.Default.WorkflowInventory);
        var next = sync.CreateSnapshot(before, 2);
        Assert.Equal("DES2"u8.ToArray(), next.Bytes[..4]);
        var legacy = next.Bytes[..BaselineStateOffset].Concat(next.Bytes[SyncHeaderSize..]).ToArray();
        legacy[3] = (byte)'1';
        var rejected = sync.Apply(before, 1, legacy);
        Assert.Equal(DeepSyncStatus.InvalidPacket, rejected.Status);
        Assert.Contains("DES1", rejected.Issue!.Message);
        next.Bytes[BaselineStateOffset] = 1;
        Assert.Equal(DeepSyncStatus.InvalidPacket, sync.Apply(before, 1, next.Bytes).Status);
    }

    [Fact]
    public void WireDigestIsSha256OfCanonicalSnapshotAndCountsTowardTheSizeLimit()
    {
        var value = new WorkflowInventory { Locations = new() { ["B"] = 2, ["A"] = 1 } };
        var typeInfo = InventoryJson.Default.WorkflowInventory;
        var snapshot = value.CreateDeepSync(typeInfo).CreateSnapshot(value, 3);
        var canonical = """{"Lines":[],"Locations":{"A":1,"B":2},"Name":"","Timestamp":0}"""u8;
        Assert.Equal(SHA256.HashData(canonical), snapshot.Bytes[ResultStateOffset..SyncHeaderSize]);

        var exact = value.CreateDeepSync(typeInfo, new DeepSyncOptions { MaxPacketBytes = snapshot.Bytes.Length });
        Assert.Equal(DeepSyncStatus.Applied, exact.Apply(value, 1, exact.CreateSnapshot(value, 3).Bytes).Status);
        var tooSmall = value.CreateDeepSync(typeInfo, new DeepSyncOptions { MaxPacketBytes = snapshot.Bytes.Length - 1 });
        Assert.Equal(DeepSyncStatus.InvalidPacket, tooSmall.Apply(value, 1, snapshot.Bytes).Status);
        Assert.Throws<InvalidOperationException>(() => tooSmall.CreateSnapshot(value, 3));
    }

    [Fact]
    public void CanonicalOrderingUsesOrdinalNamesForEscapedAndSupplementaryCharacters()
    {
        var value = new WorkflowInventory
        {
            Name = "Warehouse <north>",
            Locations = new() { ["\uE000"] = 1, ["\U00010000"] = 2, ["a\nb"] = 3, ["z"] = 4 }
        };
        var pretty = new InventoryJson(new JsonSerializerOptions { WriteIndented = true });
        var sync = value.CreateDeepSync(pretty.WorkflowInventory);
        var message = sync.CreateSnapshot(value, 2);

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteStartArray("Lines");
        writer.WriteEndArray();
        writer.WriteStartObject("Locations");
        foreach (var item in value.Locations.OrderBy(item => item.Key, StringComparer.Ordinal))
            writer.WriteNumber(item.Key, item.Value);
        writer.WriteEndObject();
        writer.WriteString("Name", value.Name);
        writer.WriteNumber("Timestamp", 0);
        writer.WriteEndObject();
        writer.Flush();
        Assert.Equal(SHA256.HashData(buffer.WrittenSpan), message.Bytes[ResultStateOffset..SyncHeaderSize]);

        var compact = value.CreateDeepSync(InventoryJson.Default.WorkflowInventory);
        Assert.Equal(DeepSyncStatus.Applied, compact.Apply(null, 1, message.Bytes).Status);
    }

    [Fact]
    public void JsonIgnoredStateCannotHideAComparedChangeFromVerification()
    {
        var before = new PartialSnapshotModel { Count = 1 };
        var after = new PartialSnapshotModel { Count = 2 };
        var sync = before.CreateDeepSync(InventoryJson.Default.PartialSnapshotModel);
        var update = sync.CreateUpdate(before, after, 3, 4);
        var drifted = new PartialSnapshotModel { Count = 1, ComparedButNotSerialized = 5 };
        var result = sync.Apply(drifted, 3, update.Bytes);
        Assert.Equal(DeepSyncStatus.StateMismatch, result.Status);
        Assert.Same(drifted, result.Value);
        Assert.Equal(3, result.Revision);
        Assert.Equal(1, drifted.Count);
        Assert.Throws<InvalidOperationException>(() => sync.CreateUpdate(before, drifted, 3, 4));

        var invalidDelta = before.ComputeDeepDelta(new PartialSnapshotModel { Count = 2, ComparedButNotSerialized = 5 });
        var buffer = new ArrayBufferWriter<byte>();
        BinaryDeltaCodec.Write(invalidDelta, buffer);
        var altered = update.Bytes[..SyncHeaderSize].Concat(buffer.WrittenSpan.ToArray()).ToArray();
        Assert.Equal(DeepSyncStatus.StateMismatch, sync.Apply(before, 3, altered).Status);
        Assert.Equal(0, before.ComparedButNotSerialized);
    }

    [Fact]
    public void AmbiguousSerializedPropertyNamesCannotBeFingerprinted()
    {
        var before = new WorkflowInventory { Locations = new() { ["A"] = 1, ["a"] = 2 } };
        var context = new InventoryJson(new JsonSerializerOptions { DictionaryKeyPolicy = JsonNamingPolicy.CamelCase });
        var sync = before.CreateDeepSync(context.WorkflowInventory);
        Assert.Throws<JsonException>(() => sync.CreateSnapshot(before, 3));
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
        var corrupted = valid.Bytes[..SyncHeaderSize].Concat(buffer.WrittenSpan.ToArray()).ToArray();
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

[DeepComparable(GenerateDelta = true)]
public sealed class PartialSnapshotModel
{
    public int Count { get; set; }
    [JsonIgnore]
    public int ComparedButNotSerialized { get; set; }
}

[JsonSerializable(typeof(WorkflowInventory))]
[JsonSerializable(typeof(WorkflowLine))]
[JsonSerializable(typeof(List<WorkflowLine>))]
[JsonSerializable(typeof(GuardedWorkflow))]
[JsonSerializable(typeof(PartialSnapshotModel))]
internal partial class InventoryJson : JsonSerializerContext;
