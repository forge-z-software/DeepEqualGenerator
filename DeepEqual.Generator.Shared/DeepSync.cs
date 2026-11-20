using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DeepEqual.Generator.Shared;

namespace DeepEqual;

public enum DeepSyncStatus
{
    Applied,
    AlreadyApplied,
    StaleBaseline,
    SchemaMismatch,
    InvalidPacket,
    InvalidOperation,
    StateMismatch
}

public sealed record DeepSyncResult<T>(DeepSyncStatus Status, T? Value, long Revision, DeepPatchIssue? Issue = null)
{
    public bool Accepted => Status is DeepSyncStatus.Applied or DeepSyncStatus.AlreadyApplied;
}

public sealed record DeepSyncMessage(byte[] Bytes, bool IsSnapshot, DeepPatchIssue? SnapshotReason = null);

public sealed record DeepSyncInspection(long BaselineRevision, long Revision, bool IsSnapshot,
    DeepChangeReport Operations);

public sealed class DeepSyncOptions
{
    public int MaxPacketBytes { get; init; } = 16 * 1024 * 1024;
    public int ContractVersion { get; init; } = 1;
    public ComparisonProfile? Profile { get; init; }
    public BinaryDeltaTypeRegistry? ValueTypes { get; init; }
}

/// <summary>Stages revisioned patches on isolated snapshots using generated model bindings.</summary>
public sealed class DeepSync<T>
{
    private const uint LegacyMagic = 0x31534544; // DES1 in little-endian byte order.
    private const uint Magic = 0x32534544; // DES2 in little-endian byte order.
    private const int BaselineStateOffset = 29;
    private const int ResultStateOffset = BaselineStateOffset + DeepSyncStateDigest.Size;
    private const int HeaderSize = ResultStateOffset + DeepSyncStateDigest.Size;
    private const byte Patch = 0;
    private const byte Snapshot = 1;
    private readonly JsonTypeInfo<T> _snapshot;
    private readonly Func<T?, T?, ComparisonOptions, bool> _equal;
    private readonly Func<T?, T?, ComparisonContext, DeltaDocument> _compute;
    private readonly Func<T?, DeltaDocument, T?> _apply;
    private readonly DeepPatchValidator _validator;
    private readonly BinaryDeltaOptions _binary;
    private readonly ComparisonOptions _comparison;
    private readonly ComparisonOptions _deltaComparison;
    private readonly int _maxPacketBytes;

    public DeepSync(string generatedContract, JsonTypeInfo<T> snapshot, DeepSyncOptions? options,
        Func<T?, T?, ComparisonOptions, bool> equal, Func<T?, T?, ComparisonContext, DeltaDocument> compute,
        Func<T?, DeltaDocument, T?> apply, DeepPatchSchema[] schemas)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        options ??= new();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaxPacketBytes, HeaderSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.ContractVersion);
        _snapshot = snapshot;
        _equal = equal;
        _compute = compute;
        _apply = apply;
        _validator = new(schemas);
        _comparison = options.Profile?.Options ?? new ComparisonOptions();
        _deltaComparison = new ComparisonOptions
        {
            StringComparison = _comparison.StringComparison,
            TreatNaNEqual = _comparison.TreatNaNEqual,
            DoubleEpsilon = _comparison.DoubleEpsilon,
            FloatEpsilon = _comparison.FloatEpsilon,
            DecimalEpsilon = _comparison.DecimalEpsilon,
            UseDirtyTracking = false
        };
        _maxPacketBytes = options.MaxPacketBytes;
        _binary = new BinaryDeltaOptions { ValueTypes = options.ValueTypes };
        _binary.Safety.MaxStringBytes = _maxPacketBytes - HeaderSize;
        _binary.Safety.MaxNesting = 64;
        var members = string.Join("|", snapshot.Properties.OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => p.Name + ":" + p.PropertyType));
        var culture = _comparison.StringComparison is StringComparison.CurrentCulture or StringComparison.CurrentCultureIgnoreCase
            ? CultureInfo.CurrentCulture.Name : "";
        var settings = FormattableString.Invariant(
            $"{options.ContractVersion}:{_comparison.StringComparison}:{culture}:{_comparison.TreatNaNEqual}:{_comparison.DoubleEpsilon:R}:{_comparison.FloatEpsilon:R}:{_comparison.DecimalEpsilon}");
        var signature = generatedContract + "\n" + typeof(T) + "\n" + members + "\n" + settings + "\n" + options.ValueTypes?.Describe();
        SchemaFingerprint = BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(signature)));
    }

    public ulong SchemaFingerprint { get; }

    public DeepSyncMessage CreateUpdate(T? before, T? after, long baselineRevision, long revision)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(baselineRevision);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(revision, baselineRevision);

        var desired = Serialize(after);
        var desiredCopy = DeserializeFaithfulCopy(after, desired);
        Span<byte> baselineState = stackalloc byte[DeepSyncStateDigest.Size];
        Span<byte> resultState = stackalloc byte[DeepSyncStateDigest.Size];
        ComputeDigest(desired, resultState);

        var delta = _equal(before, after, _comparison)
            ? DeltaDocument.ForRoot<T>()
            : _compute(before, after, new ComparisonContext(_deltaComparison));
        var buffer = new ArrayBufferWriter<byte>();
        DeepPatchIssue? issue = null;
        try
        {
            if (!delta.IsEmpty)
                issue = _validator.Validate(typeof(T), before, delta);

            if (issue is null)
            {
                var baseline = Serialize(before);
                if (baseline.AsSpan().SequenceEqual(desired))
                    resultState.CopyTo(baselineState);
                else
                    ComputeDigest(baseline, baselineState);
                var copy = DeserializeFaithfulCopy(before, baseline);
                BinaryDeltaCodec.Write(delta, buffer, _binary);
                // Check the transmitted representation and the same apply path the receiver will use.
                var encoded = BinaryDeltaCodec.Read(buffer.WrittenSpan, _binary);
                var candidate = Stage(before, copy, encoded);
                if (!_equal(candidate, after, _comparison) ||
                    !MatchesState(encoded.IsEmpty ? baseline : Serialize(candidate), resultState, desired))
                    issue = new("$", "The encoded patch did not reproduce the desired snapshot state.");
            }
        }
        catch (Exception error) when (IsDataError(error))
        {
            issue = FindBinaryIssue(delta) ?? new DeepPatchIssue("$", "The patch could not be staged.");
        }

        if (buffer.WrittenCount > _maxPacketBytes - HeaderSize)
            issue = new("$", "The patch exceeds the packet limit.");
        if (issue is not null) return WriteSnapshot(desiredCopy, revision, desired, resultState, issue);
        return WriteMessage(Patch, baselineRevision, revision, baselineState, resultState, buffer.WrittenSpan);
    }

    public DeepSyncMessage CreateSnapshot(T? desired, long revision)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        var payload = Serialize(desired);
        Span<byte> resultState = stackalloc byte[DeepSyncStateDigest.Size];
        ComputeDigest(payload, resultState);
        return WriteSnapshot(DeserializeFaithfulCopy(desired, payload), revision, payload, resultState);
    }

    private DeepSyncMessage WriteSnapshot(T? reconstructed, long revision, byte[] payload, ReadOnlySpan<byte> resultState,
        DeepPatchIssue? issue = null)
    {
        if (!MatchesState(Serialize(reconstructed), resultState, payload))
            throw new InvalidOperationException("The configured snapshot serializer changes the state during a round trip.");
        return WriteMessage(Snapshot, -1, revision, default, resultState, payload, issue);
    }

    /// <summary>Returns the candidate receiver state while leaving the supplied model untouched.</summary>
    public DeepSyncResult<T> Preview(T? current, long currentRevision, ReadOnlySpan<byte> packet)
    {
        if (!TryReadHeader(packet, out var header))
        {
            var message = packet.Length >= sizeof(uint) && BinaryPrimitives.ReadUInt32LittleEndian(packet) == LegacyMagic
                ? "DES1 packets lack state verification. Upgrade both endpoints and send a new snapshot."
                : "Invalid sync packet header or size.";
            return new(DeepSyncStatus.InvalidPacket, current, currentRevision, new("$", message));
        }
        if (header.Schema != SchemaFingerprint)
            return new(DeepSyncStatus.SchemaMismatch, current, currentRevision, new("$", "Model or comparison contract differs."));
        if (header.Revision < currentRevision ||
            (header.Revision != currentRevision && header.Kind == Patch && header.Baseline != currentRevision))
            return new(DeepSyncStatus.StaleBaseline, current, currentRevision,
                new("$", $"The receiver is at revision {currentRevision}; the packet requires revision {header.Baseline}."));

        try
        {
            var resultState = packet.Slice(ResultStateOffset, DeepSyncStateDigest.Size);
            if (header.Revision == currentRevision)
            {
                var existing = Serialize(current);
                if (!MatchesState(existing, resultState) || !PreservesState(current, existing))
                    return StateMismatch(current, currentRevision, "The current revision contains a different state.");
                return new(DeepSyncStatus.AlreadyApplied, current, currentRevision);
            }

            var payload = packet[HeaderSize..];
            if (header.Kind == Snapshot)
            {
                var snapshot = JsonSerializer.Deserialize(payload, _snapshot);
                return MatchesState(Serialize(snapshot), resultState)
                    ? new(DeepSyncStatus.Applied, snapshot, header.Revision)
                    : StateMismatch(current, currentRevision, "The decoded snapshot does not match the expected state.");
            }

            var baseline = Serialize(current);
            Span<byte> baselineState = stackalloc byte[DeepSyncStateDigest.Size];
            ComputeDigest(baseline, baselineState);
            if (!baselineState.SequenceEqual(packet.Slice(BaselineStateOffset, DeepSyncStateDigest.Size)))
                return StateMismatch(current, currentRevision, "The receiver's state differs from the expected baseline.");

            var copy = JsonSerializer.Deserialize(baseline, _snapshot);
            if (!_equal(current, copy, _comparison))
                return StateMismatch(current, currentRevision, "The snapshot serializer does not preserve the receiver's compared state.");
            var delta = BinaryDeltaCodec.Read(payload, _binary);
            var issue = _validator.Validate(typeof(T), current, delta);
            if (issue is not null) return new(DeepSyncStatus.InvalidOperation, current, currentRevision, issue);
            if (delta.IsEmpty)
                return baselineState.SequenceEqual(resultState)
                    ? new(DeepSyncStatus.Applied, current, header.Revision)
                    : StateMismatch(current, currentRevision, "The empty patch does not reproduce the expected state.");

            var candidate = Stage(current, copy, delta);
            var staged = Serialize(candidate);
            if (!MatchesState(staged, resultState) || !PreservesState(candidate, staged))
                return StateMismatch(current, currentRevision, "The staged patch does not produce the expected state.");
            return new(DeepSyncStatus.Applied, candidate, header.Revision);
        }
        catch (Exception error) when (IsDataError(error))
        {
            return new(DeepSyncStatus.InvalidOperation, current, currentRevision,
                new("$", "The payload could not be decoded or applied to an isolated snapshot."));
        }
    }

    private static DeepSyncResult<T> StateMismatch(T? current, long revision, string message) =>
        new(DeepSyncStatus.StateMismatch, current, revision, new("$", message));

    /// <summary>Produces the state and revision to commit together when application succeeds.</summary>
    public DeepSyncResult<T> Apply(T? current, long currentRevision, ReadOnlySpan<byte> packet) =>
        Preview(current, currentRevision, packet);

    public DeepSyncInspection Inspect(ReadOnlySpan<byte> packet, int maxOperations = 100)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOperations);
        if (!TryReadHeader(packet, out var header) || header.Schema != SchemaFingerprint)
            throw new InvalidOperationException("The sync packet does not match this model contract.");
        var operations = new List<DeepMismatch>();
        var truncated = false;
        if (header.Kind == Patch)
        {
            var delta = BinaryDeltaCodec.Read(packet[HeaderSize..], _binary);
            foreach (var (path, operation) in _validator.Describe(typeof(T), delta))
            {
                if (operations.Count == maxOperations)
                {
                    truncated = true;
                    break;
                }
                var kind = operation.Kind switch
                {
                    DeltaKind.DictRemove or DeltaKind.SeqRemoveAt => DeepMismatchKind.Removed,
                    DeltaKind.SeqAddAt => DeepMismatchKind.Added,
                    _ => DeepMismatchKind.Set
                };
                var before = kind == DeepMismatchKind.Removed ? operation.Value : null;
                var after = kind == DeepMismatchKind.Removed ? null : operation.Value;
                operations.Add(new DeepMismatch(path, before, after, kind));
            }
        }
        return new(header.Baseline, header.Revision, header.Kind == Snapshot, new(operations.ToArray(), [], truncated));
    }

    public DeepPatchIssue? CheckBinaryPatch(DeltaDocument delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        try
        {
            var buffer = new ArrayBufferWriter<byte>();
            BinaryDeltaCodec.Write(delta, buffer, _binary);
            return buffer.WrittenCount <= _maxPacketBytes - HeaderSize
                ? null : new("$", "The encoded patch is larger than the configured packet limit.");
        }
        catch (Exception error) when (IsDataError(error))
        {
            return FindBinaryIssue(delta) ?? new DeepPatchIssue("$", "The patch cannot be encoded.");
        }
    }

    private DeepPatchIssue? FindBinaryIssue(DeltaDocument delta)
    {
        foreach (var (path, operation) in _validator.Describe(typeof(T), delta))
        {
            var op = new DeltaDocument();
            op.Ops.Add(operation);
            try
            {
                BinaryDeltaCodec.Write(op, new ArrayBufferWriter<byte>(), _binary);
            }
            catch (Exception error) when (IsDataError(error))
            {
                return new(path, "Value cannot be encoded. Register its serializer metadata or send a snapshot.");
            }
        }
        return null;
    }

    private T? Stage(T? original, T? copy, DeltaDocument document)
    {
        if (document.IsEmpty) return copy;
        if (copy is not null && ReferenceEquals(original, copy))
            throw new InvalidOperationException("The snapshot serializer must create an independent copy.");
        var issue = _validator.Validate(typeof(T), copy, document);
        if (issue is not null) throw new InvalidOperationException(issue.Message);
        return _apply(copy, document);
    }

    private T? DeserializeFaithfulCopy(T? original, byte[] serialized)
    {
        var copy = JsonSerializer.Deserialize(serialized, _snapshot);
        if (!_equal(original, copy, _comparison))
            throw new InvalidOperationException("The configured snapshot serializer does not preserve the compared model state.");
        return copy;
    }

    private bool PreservesState(T? original, byte[] serialized) =>
        _equal(original, JsonSerializer.Deserialize(serialized, _snapshot), _comparison);

    private byte[] Serialize(T? value) => JsonSerializer.SerializeToUtf8Bytes(value, (JsonTypeInfo)_snapshot);

    private void ComputeDigest(byte[] serialized, Span<byte> destination) =>
        DeepSyncStateDigest.Compute(serialized, _snapshot.Options.MaxDepth, destination);

    private bool MatchesState(byte[] serialized, ReadOnlySpan<byte> expected, byte[]? knownState = null)
    {
        if (knownState is not null && serialized.AsSpan().SequenceEqual(knownState)) return true;
        Span<byte> actual = stackalloc byte[DeepSyncStateDigest.Size];
        ComputeDigest(serialized, actual);
        return actual.SequenceEqual(expected);
    }

    private DeepSyncMessage WriteMessage(byte kind, long baseline, long revision, ReadOnlySpan<byte> baselineState,
        ReadOnlySpan<byte> resultState, ReadOnlySpan<byte> payload, DeepPatchIssue? issue = null)
    {
        if (payload.Length > _maxPacketBytes - HeaderSize)
            throw new InvalidOperationException("The sync payload is larger than the configured packet limit.");
        var packet = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, Magic);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(4), SchemaFingerprint);
        BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(12), baseline);
        BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(20), revision);
        packet[28] = kind;
        baselineState.CopyTo(packet.AsSpan(BaselineStateOffset, DeepSyncStateDigest.Size));
        resultState.CopyTo(packet.AsSpan(ResultStateOffset, DeepSyncStateDigest.Size));
        payload.CopyTo(packet.AsSpan(HeaderSize));
        return new(packet, kind == Snapshot, issue);
    }

    private bool TryReadHeader(ReadOnlySpan<byte> packet, out Header header)
    {
        header = default;
        if (packet.Length <= HeaderSize || packet.Length > _maxPacketBytes ||
            BinaryPrimitives.ReadUInt32LittleEndian(packet) != Magic)
            return false;
        var schema = BinaryPrimitives.ReadUInt64LittleEndian(packet[4..]);
        var baseline = BinaryPrimitives.ReadInt64LittleEndian(packet[12..]);
        var revision = BinaryPrimitives.ReadInt64LittleEndian(packet[20..]);
        var kind = packet[28];
        if (revision < 0 || (kind == Snapshot
                ? baseline != -1 || packet.Slice(BaselineStateOffset, DeepSyncStateDigest.Size).ContainsAnyExcept((byte)0)
                : kind != Patch || baseline < 0 || revision <= baseline))
            return false;
        header = new(schema, baseline, revision, kind);
        return true;
    }

    private static bool IsDataError(Exception error) => error is
        ArgumentException or InvalidOperationException or JsonException or NotSupportedException or InvalidCastException
        or IndexOutOfRangeException or NullReferenceException or FormatException or OverflowException or TypeLoadException;

    private readonly record struct Header(ulong Schema, long Baseline, long Revision, byte Kind);
}
