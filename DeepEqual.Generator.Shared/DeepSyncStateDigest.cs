using System;
using System.Buffers;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeepEqual;

internal static class DeepSyncStateDigest
{
    internal const int Size = 32;

    internal static void Compute(byte[] serialized, int maxDepth, Span<byte> destination)
    {
        var options = new JsonReaderOptions { MaxDepth = maxDepth };
        var reader = new Utf8JsonReader(serialized, options);
        if (!reader.Read()) throw new JsonException("A snapshot must contain a JSON value.");
        using var hash = new DigestBuffer();
        using var writer = new Utf8JsonWriter(hash, new JsonWriterOptions { MaxDepth = maxDepth });
        var order = new PropertyOrder(serialized);
        WriteCanonical(order, ref reader, 0, writer, options);
        if (reader.Read()) throw new JsonException("A snapshot must contain a single JSON value.");
        writer.Flush();
        hash.Finish(destination);
    }

    private static void WriteCanonical(PropertyOrder order, ref Utf8JsonReader reader, int offset,
        Utf8JsonWriter writer, JsonReaderOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                writer.WriteStartObject();
                WriteProperties(order, ref reader, offset, writer, options);
                writer.WriteEndObject();
                break;
            case JsonTokenType.StartArray:
                writer.WriteStartArray();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    WriteCanonical(order, ref reader, offset, writer, options);
                writer.WriteEndArray();
                break;
            case JsonTokenType.String:
                if (reader.ValueIsEscaped)
                    writer.WriteStringValue(reader.GetString());
                else
                    writer.WriteStringValue(reader.ValueSpan);
                break;
            case JsonTokenType.Number:
                writer.WriteRawValue(reader.ValueSpan, skipInputValidation: true);
                break;
            case JsonTokenType.True:
            case JsonTokenType.False:
                writer.WriteBooleanValue(reader.TokenType == JsonTokenType.True);
                break;
            case JsonTokenType.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException("Unexpected token in snapshot JSON.");
        }
    }

    private static void WriteProperties(PropertyOrder order, ref Utf8JsonReader reader, int offset,
        Utf8JsonWriter writer, JsonReaderOptions options)
    {
        if (!reader.Read() || reader.TokenType == JsonTokenType.EndObject) return;
        var first = ReadProperty(ref reader, offset);
        if (!reader.Read() || reader.TokenType == JsonTokenType.EndObject)
        {
            WriteProperty(order, first, writer, options);
            return;
        }

        var properties = ArrayPool<Property>.Shared.Rent(16);
        properties[0] = first;
        var count = 1;
        var needsOrdinalStrings = first.Name is not null;
        try
        {
            do
            {
                if (count == properties.Length)
                {
                    var larger = ArrayPool<Property>.Shared.Rent(checked(count * 2));
                    Array.Copy(properties, larger, count);
                    ArrayPool<Property>.Shared.Return(properties, clearArray: true);
                    properties = larger;
                }

                var property = ReadProperty(ref reader, offset);
                needsOrdinalStrings |= property.Name is not null;
                properties[count++] = property;
            }
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject);

            // ASCII names have the same ordering in UTF-8 as in ordinal UTF-16.
            if (needsOrdinalStrings)
                for (var i = 0; i < count; i++)
                    if (properties[i].Name is null)
                        properties[i] = properties[i] with { Name = Encoding.UTF8.GetString(order.GetName(properties[i])) };
            Array.Sort(properties, 0, count, order);
            for (var i = 0; i < count; i++)
            {
                var property = properties[i];
                if (i > 0 && order.Compare(properties[i - 1], property) == 0)
                    throw new JsonException("Snapshot objects must have unique JSON property names.");
                WriteProperty(order, property, writer, options);
            }
        }
        finally
        {
            ArrayPool<Property>.Shared.Return(properties, clearArray: true);
        }
    }

    private static Property ReadProperty(ref Utf8JsonReader reader, int offset)
    {
        if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Expected a snapshot property.");
        var nameStart = offset + (int)reader.TokenStartIndex + 1;
        var nameLength = reader.ValueSpan.Length;
        var name = reader.ValueIsEscaped || !Ascii.IsValid(reader.ValueSpan) ? reader.GetString() : null;
        if (!reader.Read()) throw new JsonException("Expected a snapshot property value.");
        var valueStart = (int)reader.TokenStartIndex;
        var token = reader.TokenType;
        reader.Skip();
        return new Property(nameStart, nameLength, offset + valueStart,
            (int)reader.BytesConsumed - valueStart, token, name);
    }

    private static void WriteProperty(PropertyOrder order, Property property, Utf8JsonWriter writer, JsonReaderOptions options)
    {
        if (property.Name is { } name)
            writer.WritePropertyName(name);
        else
            writer.WritePropertyName(order.GetName(property));

        var value = order.Source.AsSpan(property.ValueStart, property.ValueLength);
        if (property.ValueKind is JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False or JsonTokenType.Null)
        {
            writer.WriteRawValue(value, skipInputValidation: true);
            return;
        }

        var reader = new Utf8JsonReader(value, options);
        if (!reader.Read()) throw new JsonException("Expected a snapshot property value.");
        WriteCanonical(order, ref reader, property.ValueStart, writer, options);
    }

    private readonly record struct Property(int NameStart, int NameLength, int ValueStart, int ValueLength,
        JsonTokenType ValueKind, string? Name);

    private sealed class PropertyOrder(byte[] source) : IComparer<Property>
    {
        internal byte[] Source { get; } = source;
        internal ReadOnlySpan<byte> GetName(Property property) => Source.AsSpan(property.NameStart, property.NameLength);

        public int Compare(Property x, Property y) => x.Name is not null
            ? string.CompareOrdinal(x.Name, y.Name)
            : GetName(x).SequenceCompareTo(GetName(y));
    }

    private sealed class DigestBuffer : IBufferWriter<byte>, IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private byte[]? _buffer;

        public void Advance(int count) => _hash.AppendData(_buffer!, 0, count);
        public Memory<byte> GetMemory(int sizeHint = 0) => Rent(sizeHint);
        public Span<byte> GetSpan(int sizeHint = 0) => Rent(sizeHint);

        internal void Finish(Span<byte> destination) => _hash.GetHashAndReset(destination);

        private byte[] Rent(int sizeHint)
        {
            var size = Math.Max(sizeHint, 256);
            if (_buffer is { Length: var length } && length >= size) return _buffer;
            var buffer = ArrayPool<byte>.Shared.Rent(size);
            if (_buffer is not null) ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
            return _buffer = buffer;
        }

        public void Dispose()
        {
            _hash.Dispose();
            if (_buffer is not null) ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
        }
    }
}
