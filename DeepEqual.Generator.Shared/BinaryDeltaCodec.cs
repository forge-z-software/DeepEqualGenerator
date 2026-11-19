using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace DeepEqual.Generator.Shared;

/// <summary>Lossless binary codec for <see cref="DeltaDocument" />.</summary>
public static class BinaryDeltaCodec
{
    private const uint HeaderMagic = 0x31434442; // BDC1 in little-endian byte order.
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    public static void Write(DeltaDocument doc, IBufferWriter<byte> output, BinaryDeltaOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        new Writer(output, ValidateOptions(options)).WriteDocument(doc);
    }

    public static DeltaDocument Read(ReadOnlySpan<byte> data, BinaryDeltaOptions? options = null) =>
        new Reader(data, ValidateOptions(options)).ReadDocument();

    private static BinaryDeltaOptions ValidateOptions(BinaryDeltaOptions? options)
    {
        var profile = options ?? BinaryDeltaOptions.Default;
        if (profile.Safety.MaxOps < 0 || profile.Safety.MaxStringBytes < 0 ||
            profile.Safety.MaxNesting is < 0 or > 256 ||
            (!profile.IncludeHeader && profile.StableTypeFingerprint != 0))
            throw new ArgumentException("Invalid BinaryDelta limits or header settings.", nameof(options));
        return profile;
    }

    private static bool IsKnownOperation(DeltaKind kind) =>
        kind is DeltaKind.ReplaceObject or DeltaKind.SetMember or DeltaKind.NestedMember or
            DeltaKind.SeqReplaceAt or DeltaKind.SeqAddAt or DeltaKind.SeqRemoveAt or DeltaKind.SeqNestedAt or
            DeltaKind.DictSet or DeltaKind.DictRemove or DeltaKind.DictNested;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteGuid16(ref SpanWriter sw, in Guid g)
    {
        Span<byte> tmp = stackalloc byte[16];
        g.TryWriteBytes(tmp);
        sw.WriteUInt64(BinaryPrimitives.ReadUInt64LittleEndian(tmp));
        sw.WriteUInt64(BinaryPrimitives.ReadUInt64LittleEndian(tmp.Slice(8)));
    }


    private enum VTag : byte
    {
        Null = 0,
        BoolFalse = 1,
        BoolTrue = 2,
        SByte = 3,
        Byte = 4,
        Int16 = 5,
        UInt16 = 6,
        Int32 = 7,
        UInt32 = 8,
        Int64 = 9,
        UInt64 = 10,
        Char16 = 11,
        Single = 12,
        Double = 13,
        Decimal = 14,
        StringInline = 15,
        StringRef = 16,
        Guid16 = 17,
        DateTimeBin64 = 18,
        TimeSpanTicks = 19,
        DateTimeOffset = 20,
        Enum = 21,
        ByteArray = 22,
        Array = 23,
        List = 24,
        Dictionary = 25
    }

    private enum TypeSpecKind : byte
    {
        PrimitiveOrKnown = 0,
        Enum = 1,
        Object = 2
    }

    private enum KnownTypeCode : byte
    {
        SByte,
        Byte,
        Int16,
        UInt16,
        Int32,
        UInt32,
        Int64,
        UInt64,
        Single,
        Double,
        Decimal,
        Bool,
        Char,
        String,
        Guid,
        DateTime,
        TimeSpan,
        DateTimeOffset
    }


    private ref struct Writer
    {
        private readonly IBufferWriter<byte> _out;
        private readonly BinaryDeltaOptions _opt;

        private Dictionary<string, StringEntry>? _stringEntries;
        private List<EncodedString>? _strings;
        private Dictionary<Type, int>? _typeToId;
        private List<TypeEntry>? _types;

        private int _nesting;

        internal Writer(IBufferWriter<byte> output, BinaryDeltaOptions? opt)
        {
            _out = output ?? throw new ArgumentNullException(nameof(output));
            _opt = opt ?? BinaryDeltaOptions.Default;
            _stringEntries = null;
            _strings = null;
            _typeToId = null;
            _types = null;
            _nesting = 0;
        }

        public void WriteDocument(DeltaDocument doc)
        {
            ScanDocument(doc);
            var sw = new SpanWriter(_out);
            if (_opt.IncludeHeader)
            {
                sw.WriteUInt32(HeaderMagic);
                sw.WriteVarUInt(1u);
                sw.WriteVarUInt(_opt.StableTypeFingerprint);
                var flags = (byte)((_strings is { Count: > 0 } ? 0b01 : 0) |
                                   (_types is { Count: > 0 } ? 0b10 : 0));
                sw.WriteByte(flags);

                if (_strings is { Count: > 0 } strings)
                {
                    sw.WriteVarUInt((uint)strings.Count);
                    foreach (var entry in strings)
                        sw.WriteUtf8StringInline(entry.Value, entry.ByteCount);
                }

                if (_types is { Count: > 0 } types)
                {
                    sw.WriteVarUInt((uint)types.Count);
                    foreach (var entry in types)
                        WriteTypeEntry(ref sw, in entry);
                }
            }

            sw.WriteVarUInt((uint)doc.Ops.Count);
            foreach (ref readonly var op in CollectionsMarshal.AsSpan(doc.Ops))
                WriteOp(ref sw, in op);
            sw.Commit();
        }

        private void WriteOp(ref SpanWriter sw, in DeltaOp op)
        {
            sw.WriteVarUInt((uint)op.Kind);
            sw.WriteVarInt(op.MemberIndex);

            switch (op.Kind)
            {
                case DeltaKind.SetMember:
                case DeltaKind.ReplaceObject:
                    WriteValue(ref sw, op.Value);
                    break;
                case DeltaKind.DictSet:
                    WriteValue(ref sw, op.Key);
                    WriteValue(ref sw, op.Value);
                    break;
                case DeltaKind.DictRemove:
                    WriteValue(ref sw, op.Key);
                    break;
                case DeltaKind.DictNested:
                    WriteValue(ref sw, op.Key);
                    WriteNested(ref sw, op.Nested!);
                    break;
                case DeltaKind.SeqReplaceAt:
                case DeltaKind.SeqAddAt:
                case DeltaKind.SeqRemoveAt:
                    sw.WriteVarUInt((uint)op.Index);
                    WriteValue(ref sw, op.Value);
                    break;
                case DeltaKind.SeqNestedAt:
                    sw.WriteVarUInt((uint)op.Index);
                    goto case DeltaKind.NestedMember;
                case DeltaKind.NestedMember:
                    WriteNested(ref sw, op.Nested!);
                    break;
            }
        }

        private void WriteNested(ref SpanWriter sw, DeltaDocument nested)
        {
            if (++_nesting > _opt.Safety.MaxNesting) throw new InvalidOperationException("Max nesting exceeded");

            sw.WriteVarUInt((uint)nested.Ops.Count);
            foreach (var op in nested.Ops) WriteOp(ref sw, op);

            _nesting--;
        }

        private void WriteValue(ref SpanWriter sw, object? value)
        {
            if (value is null)
            {
                sw.WriteByte((byte)VTag.Null);
                return;
            }

            switch (value)
            {
                case bool b:
                    sw.WriteByte((byte)(b ? VTag.BoolTrue : VTag.BoolFalse));
                    return;

                case sbyte sb:
                    sw.WriteByte((byte)VTag.SByte);
                    sw.WriteVarInt(sb);
                    return;

                case byte b8:
                    sw.WriteByte((byte)VTag.Byte);
                    sw.WriteVarUInt(b8);
                    return;

                case short i16:
                    sw.WriteByte((byte)VTag.Int16);
                    sw.WriteVarInt(i16);
                    return;

                case ushort u16:
                    sw.WriteByte((byte)VTag.UInt16);
                    sw.WriteVarUInt(u16);
                    return;

                case int i32:
                    sw.WriteByte((byte)VTag.Int32);
                    sw.WriteVarInt(i32);
                    return;

                case uint u32:
                    sw.WriteByte((byte)VTag.UInt32);
                    sw.WriteVarUInt(u32);
                    return;

                case long i64:
                    sw.WriteByte((byte)VTag.Int64);
                    sw.WriteVarInt(i64);
                    return;

                case ulong u64:
                    sw.WriteByte((byte)VTag.UInt64);
                    sw.WriteVarUInt(u64);
                    return;

                case char ch:
                    sw.WriteByte((byte)VTag.Char16);
                    sw.WriteUInt16(ch);
                    return;

                case float f32:
                    sw.WriteByte((byte)VTag.Single);
                    sw.WriteUInt32((uint)BitConverter.SingleToInt32Bits(f32));
                    return;

                case double f64:
                    sw.WriteByte((byte)VTag.Double);
                    sw.WriteUInt64((ulong)BitConverter.DoubleToInt64Bits(f64));
                    return;

                case decimal dec:
                    WriteDecimal(ref sw, dec);
                    return;

                case string s:
                    if (_stringEntries is not null && _stringEntries.TryGetValue(s, out var entry))
                    {
                        if (entry.TableIndex >= 0)
                        {
                            sw.WriteByte((byte)VTag.StringRef);
                            sw.WriteVarUInt((uint)entry.TableIndex);
                        }
                        else
                        {
                            sw.WriteByte((byte)VTag.StringInline);
                            sw.WriteUtf8StringInline(s, entry.ByteCount);
                        }
                    }
                    else
                    {
                        sw.WriteByte((byte)VTag.StringInline);
                        sw.WriteUtf8StringInline(s);
                    }

                    return;

                case Guid g:
                    sw.WriteByte((byte)VTag.Guid16);
                    WriteGuid16(ref sw, g);
                    return;

                case DateTime dt:
                    sw.WriteByte((byte)VTag.DateTimeBin64);
                    sw.WriteVarInt(dt.ToBinary());
                    return;

                case TimeSpan ts:
                    sw.WriteByte((byte)VTag.TimeSpanTicks);
                    sw.WriteVarInt(ts.Ticks);
                    return;

                case DateTimeOffset dto:
                    sw.WriteByte((byte)VTag.DateTimeOffset);
                    sw.WriteVarInt(dto.Ticks);
                    sw.WriteVarInt((int)dto.Offset.TotalMinutes);
                    return;
            }

            var t = value.GetType();

            if (t.IsEnum)
            {
                var underlying = Enum.GetUnderlyingType(t);
                if (_opt.IncludeEnumTypeIdentity)
                {
                    sw.WriteByte((byte)VTag.Enum);
                    WriteEnumTypeIdentity(ref sw, t);
                    WriteEnumUnderlying(ref sw, value, underlying);
                }
                else
                {
                    WriteEnumAsPrimitive(ref sw, value, underlying);
                }

                return;
            }

            if (value is byte[] blob)
            {
                sw.WriteByte((byte)VTag.ByteArray);
                sw.WriteVarUInt((uint)blob.Length);
                sw.WriteBytes(blob);
                return;
            }

            if (value is Array { Rank: 1 } arr)
            {
                sw.WriteByte((byte)VTag.Array);
                var elemT = t.GetElementType() ?? typeof(object);
                WriteTypeSpec(ref sw, elemT);
                var len = arr.Length;
                sw.WriteVarUInt((uint)len);
                for (var i = 0; i < len; i++) WriteValue(ref sw, arr.GetValue(i));

                return;
            }

            if (IsConcreteList(value, out var elem))
            {
                sw.WriteByte((byte)VTag.List);
                WriteTypeSpec(ref sw, elem);
                var il = (IList)value;
                var cnt = il.Count;
                sw.WriteVarUInt((uint)cnt);
                for (var i = 0; i < cnt; i++) WriteValue(ref sw, il[i]);

                return;
            }

            if (IsConcreteDictionary(value, out var k, out var v))
            {
                sw.WriteByte((byte)VTag.Dictionary);
                WriteTypeSpec(ref sw, k);
                WriteTypeSpec(ref sw, v);
                var id = (IDictionary)value;
                sw.WriteVarUInt((uint)id.Count);
                foreach (DictionaryEntry e in id)
                {
                    WriteValue(ref sw, e.Key);
                    WriteValue(ref sw, e.Value);
                }

                return;
            }

            throw new NotSupportedException(
                $"Cannot serialize value of type '{t.FullName}' in BinaryDelta (v1). Use nested deltas or extend codec.");
        }

        private static bool IsConcreteList(object o, out Type elem)
        {
            var t = o.GetType();
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
            {
                elem = t.GetGenericArguments()[0];
                return true;
            }

            elem = typeof(object);
            return false;
        }

        private static bool IsConcreteDictionary(object o, out Type key, out Type val)
        {
            var t = o.GetType();
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                var ga = t.GetGenericArguments();
                key = ga[0];
                val = ga[1];
                return true;
            }

            key = val = typeof(object);
            return false;
        }

        private static void WriteDecimal(ref SpanWriter sw, decimal v)
        {
            sw.WriteByte((byte)VTag.Decimal);
            var bits = decimal.GetBits(v);
            sw.WriteUInt32((uint)bits[0]);
            sw.WriteUInt32((uint)bits[1]);
            sw.WriteUInt32((uint)bits[2]);
            sw.WriteUInt32((uint)bits[3]);
        }

        private void WriteEnumTypeIdentity(ref SpanWriter sw, Type enumType)
        {
            sw.WriteVarUInt((uint)TypeSpecKind.Enum);
            if (_opt is { IncludeHeader: true, UseTypeTable: true } && _typeToId is not null &&
                _typeToId.TryGetValue(enumType, out var tid))
                sw.WriteVarUInt((uint)tid);
            else
                WriteInlineEnumTypeDesc(ref sw, enumType);
        }

        private static void WriteEnumAsPrimitive(ref SpanWriter sw, object boxedEnum, Type underlying)
        {
            if (underlying == typeof(sbyte))
            {
                sw.WriteByte((byte)VTag.SByte);
                sw.WriteVarInt((sbyte)boxedEnum);
                return;
            }

            if (underlying == typeof(byte))
            {
                sw.WriteByte((byte)VTag.Byte);
                sw.WriteVarUInt((byte)boxedEnum);
                return;
            }

            if (underlying == typeof(short))
            {
                sw.WriteByte((byte)VTag.Int16);
                sw.WriteVarInt((short)boxedEnum);
                return;
            }

            if (underlying == typeof(ushort))
            {
                sw.WriteByte((byte)VTag.UInt16);
                sw.WriteVarUInt((ushort)boxedEnum);
                return;
            }

            if (underlying == typeof(int))
            {
                sw.WriteByte((byte)VTag.Int32);
                sw.WriteVarInt((int)boxedEnum);
                return;
            }

            if (underlying == typeof(uint))
            {
                sw.WriteByte((byte)VTag.UInt32);
                sw.WriteVarUInt((uint)boxedEnum);
                return;
            }

            if (underlying == typeof(long))
            {
                sw.WriteByte((byte)VTag.Int64);
                sw.WriteVarInt((long)boxedEnum);
                return;
            }

            if (underlying == typeof(ulong))
            {
                sw.WriteByte((byte)VTag.UInt64);
                sw.WriteVarUInt((ulong)boxedEnum);
                return;
            }

            throw new NotSupportedException("Unsupported enum underlying type.");
        }

        private static void WriteEnumUnderlying(ref SpanWriter sw, object boxedEnum, Type underlying)
        {
            if (underlying == typeof(sbyte))
            {
                sw.WriteVarInt((sbyte)boxedEnum);
                return;
            }

            if (underlying == typeof(byte))
            {
                sw.WriteVarUInt((byte)boxedEnum);
                return;
            }

            if (underlying == typeof(short))
            {
                sw.WriteVarInt((short)boxedEnum);
                return;
            }

            if (underlying == typeof(ushort))
            {
                sw.WriteVarUInt((ushort)boxedEnum);
                return;
            }

            if (underlying == typeof(int))
            {
                sw.WriteVarInt((int)boxedEnum);
                return;
            }

            if (underlying == typeof(uint))
            {
                sw.WriteVarUInt((uint)boxedEnum);
                return;
            }

            if (underlying == typeof(long))
            {
                sw.WriteVarInt((long)boxedEnum);
                return;
            }

            if (underlying == typeof(ulong))
            {
                sw.WriteVarUInt((ulong)boxedEnum);
                return;
            }

            throw new NotSupportedException("Unsupported enum underlying type.");
        }

        private void WriteTypeSpec(ref SpanWriter sw, Type t)
        {
            if (!_opt.IncludeEnumTypeIdentity && t.IsEnum)
                t = Enum.GetUnderlyingType(t);

            if (t == typeof(object))
            {
                sw.WriteVarUInt((uint)TypeSpecKind.Object);
                return;
            }

            if (TryWriteKnownTypeSpec(ref sw, t)) return;

            if (t.IsEnum)
            {
                sw.WriteVarUInt((uint)TypeSpecKind.Enum);
                if (_opt is { IncludeHeader: true, UseTypeTable: true } && _typeToId is not null &&
                    _typeToId.TryGetValue(t, out var tid))
                    sw.WriteVarUInt((uint)tid);
                else
                    WriteInlineEnumTypeDesc(ref sw, t);

                return;
            }

            throw new NotSupportedException($"BinaryDelta does not support container element type '{t}'.");
        }

        private static bool TryWriteKnownTypeSpec(ref SpanWriter sw, Type t)
        {
            KnownTypeCode code;
            if (t == typeof(sbyte))
                code = KnownTypeCode.SByte;
            else if (t == typeof(byte))
                code = KnownTypeCode.Byte;
            else if (t == typeof(short))
                code = KnownTypeCode.Int16;
            else if (t == typeof(ushort))
                code = KnownTypeCode.UInt16;
            else if (t == typeof(int))
                code = KnownTypeCode.Int32;
            else if (t == typeof(uint))
                code = KnownTypeCode.UInt32;
            else if (t == typeof(long))
                code = KnownTypeCode.Int64;
            else if (t == typeof(ulong))
                code = KnownTypeCode.UInt64;
            else if (t == typeof(float))
                code = KnownTypeCode.Single;
            else if (t == typeof(double))
                code = KnownTypeCode.Double;
            else if (t == typeof(decimal))
                code = KnownTypeCode.Decimal;
            else if (t == typeof(bool))
                code = KnownTypeCode.Bool;
            else if (t == typeof(char))
                code = KnownTypeCode.Char;
            else if (t == typeof(string))
                code = KnownTypeCode.String;
            else if (t == typeof(Guid))
                code = KnownTypeCode.Guid;
            else if (t == typeof(DateTime))
                code = KnownTypeCode.DateTime;
            else if (t == typeof(TimeSpan))
                code = KnownTypeCode.TimeSpan;
            else if (t == typeof(DateTimeOffset))
                code = KnownTypeCode.DateTimeOffset;
            else
                return false;

            sw.WriteVarUInt((uint)TypeSpecKind.PrimitiveOrKnown);
            sw.WriteVarUInt((uint)code);
            return true;
        }

        private static void WriteInlineEnumTypeDesc(ref SpanWriter sw, Type enumType)
        {
            var asm = enumType.Assembly.GetName().Name ?? "";
            var mvid = enumType.Module.ModuleVersionId;
            sw.WriteUtf8StringInline(enumType.FullName ?? enumType.Name);
            sw.WriteUtf8StringInline(asm);

            Span<byte> tmp = stackalloc byte[16];
            mvid.TryWriteBytes(tmp);
            sw.WriteUInt64(BinaryPrimitives.ReadUInt64LittleEndian(tmp));
            sw.WriteUInt64(BinaryPrimitives.ReadUInt64LittleEndian(tmp.Slice(8)));
        }

        private readonly record struct EncodedString(string Value, int ByteCount);

        private struct StringEntry
        {
            internal int ByteCount;
            internal int TableIndex;
            internal bool Repeated;
        }

        private readonly record struct TypeEntry(string FullName, string AssemblySimpleName, Guid Mvid);

        private void ScanDocument(DeltaDocument doc)
        {
            var includeEnumIdentity = _opt.IncludeEnumTypeIdentity;
            var limits = _opt.Safety;
            var useTypeTable = _opt.IncludeHeader && _opt.UseTypeTable;
            HashSet<Type>? enumTypes = null;
            var useStringTable = _opt.IncludeHeader && _opt.UseStringTable;
            Dictionary<string, StringEntry>? counts = null;

            void RequireDepth(int depth)
            {
                if (depth > limits.MaxNesting) throw new InvalidOperationException("Max nesting exceeded.");
            }

            void RequireCount(int count)
            {
                if (count > limits.MaxOps) throw new InvalidOperationException("Bound exceeded.");
            }

            int CheckString(string value)
            {
                var bytes = Utf8.GetByteCount(value);
                if (bytes > limits.MaxStringBytes)
                    throw new InvalidOperationException("String bound exceeded.");
                return bytes;
            }

            void CountString(string value)
            {
                if (!useStringTable)
                {
                    CheckString(value);
                    return;
                }

                counts ??= new Dictionary<string, StringEntry>(StringComparer.Ordinal);
                ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(counts, value, out var exists);
                if (exists)
                    entry.Repeated = true;
                else
                    entry = new StringEntry { ByteCount = CheckString(value), TableIndex = -1 };
            }

            void VisitValue(object? v, int nesting)
            {
                switch (v)
                {
                    case null:
                        return;
                    case string s:
                        CountString(s);
                        return;
                    case int or uint or long or ulong or bool or byte or sbyte or short or ushort or
                         float or double or decimal or char or Guid or DateTime or TimeSpan or DateTimeOffset:
                        return;
                    case byte[] bytes:
                        if (bytes.Length > limits.MaxStringBytes)
                            throw new InvalidOperationException("Byte array bound exceeded.");
                        return;
                    case Array array:
                        RequireDepth(nesting);
                        if (array.Rank != 1 || array.GetLowerBound(0) != 0)
                            throw new NotSupportedException("BinaryDelta supports zero-based one-dimensional arrays.");
                        RequireCount(array.Length);
                        foreach (var item in array) VisitValue(item, nesting + 1);
                        return;
                    case IList list:
                        RequireDepth(nesting);
                        RequireCount(list.Count);
                        foreach (var item in list) VisitValue(item, nesting + 1);
                        return;
                    case IDictionary dictionary:
                        RequireDepth(nesting);
                        RequireCount(dictionary.Count);
                        foreach (DictionaryEntry entry in dictionary)
                        {
                            VisitValue(entry.Key, nesting + 1);
                            VisitValue(entry.Value, nesting + 1);
                        }
                        return;
                }

                var t = v.GetType();
                if (includeEnumIdentity && t.IsEnum)
                {
                    if (Utf8.GetByteCount(t.FullName ?? t.Name) > limits.MaxStringBytes ||
                        Utf8.GetByteCount(t.Assembly.GetName().Name ?? "") > limits.MaxStringBytes)
                        throw new InvalidOperationException("Enum type identity bound exceeded.");
                    if (useTypeTable) (enumTypes ??= new HashSet<Type>()).Add(t);
                }
            }

            void WalkDoc(DeltaDocument d, int nesting)
            {
                RequireDepth(nesting);
                RequireCount(d.Ops.Count);
                foreach (var op in d.Ops)
                {
                    if (!IsKnownOperation(op.Kind)) throw new InvalidOperationException("Unknown delta operation.");
                    if (op.Kind is DeltaKind.SeqAddAt or DeltaKind.SeqRemoveAt or DeltaKind.SeqReplaceAt or DeltaKind.SeqNestedAt && op.Index < 0)
                        throw new InvalidOperationException("Sequence index must be nonnegative.");
                    VisitValue(op.Key, nesting + 1);
                    VisitValue(op.Value, nesting + 1);

                    if (op.Kind is DeltaKind.NestedMember or DeltaKind.DictNested or DeltaKind.SeqNestedAt)
                        WalkDoc(op.Nested ?? throw new InvalidOperationException("Missing nested document."), nesting + 1);
                }
            }

            WalkDoc(doc, 0);

            if (counts is not null)
            {
                foreach (var (key, entry) in counts)
                {
                    if (!entry.Repeated && key.Length < 8) continue;

                    _strings ??= new List<EncodedString>();
                    CollectionsMarshal.GetValueRefOrNullRef(counts, key).TableIndex = _strings.Count;
                    _strings.Add(new EncodedString(key, entry.ByteCount));
                }

                if (_strings is not null) _stringEntries = counts;
            }

            RequireCount(_strings?.Count ?? 0);
            RequireCount(enumTypes?.Count ?? 0);
            if (enumTypes is { Count: > 0 })
            {
                _typeToId = new Dictionary<Type, int>(enumTypes.Count);
                _types = new List<TypeEntry>(enumTypes.Count);
                foreach (var t in enumTypes)
                {
                    var id = _types.Count;
                    _typeToId[t] = id;
                    var asm = t.Assembly.GetName().Name ?? "";
                    var mvid = t.Module.ModuleVersionId;
                    _types.Add(new TypeEntry(t.FullName ?? t.Name, asm, mvid));
                }
            }
        }

        private static void WriteTypeEntry(ref SpanWriter sw, in TypeEntry t)
        {
            sw.WriteUtf8StringInline(t.FullName);
            sw.WriteUtf8StringInline(t.AssemblySimpleName);

            Span<byte> tmp = stackalloc byte[16];
            t.Mvid.TryWriteBytes(tmp);
            sw.WriteUInt64(BinaryPrimitives.ReadUInt64LittleEndian(tmp));
            sw.WriteUInt64(BinaryPrimitives.ReadUInt64LittleEndian(tmp.Slice(8)));
        }
    }


    private ref struct Reader(ReadOnlySpan<byte> data, BinaryDeltaOptions? opt)
    {
        private ReadOnlySpan<byte> _data = data;
        private readonly BinaryDeltaOptions _opt = opt ?? BinaryDeltaOptions.Default;

        private string[]? _strings = null;
        private Type[]? _enumTypes = null;

        private int _nesting = 0;

        private DeltaOp ReadOp(ref SpanReader sr)
        {
            sr.ReadOperationPrefix(out var kind, out var memberIndex);

            switch (kind)
            {
                case DeltaKind.SetMember:
                case DeltaKind.ReplaceObject:
                    return new DeltaOp(memberIndex, kind, -1, null, ReadValue(ref sr), null);
                case DeltaKind.DictSet:
                    return new DeltaOp(memberIndex, kind, -1, ReadValue(ref sr), ReadValue(ref sr), null);
                case DeltaKind.DictRemove:
                    return new DeltaOp(memberIndex, kind, -1, ReadValue(ref sr), null, null);
                case DeltaKind.DictNested:
                    return new DeltaOp(memberIndex, kind, -1, ReadValue(ref sr), null, ReadNested(ref sr));
                case DeltaKind.SeqReplaceAt:
                case DeltaKind.SeqAddAt:
                case DeltaKind.SeqRemoveAt:
                    return new DeltaOp(memberIndex, kind, ReadIndex(ref sr), null, ReadValue(ref sr), null);
                case DeltaKind.SeqNestedAt:
                    return new DeltaOp(memberIndex, kind, ReadIndex(ref sr), null, null, ReadNested(ref sr));
                case DeltaKind.NestedMember:
                    return new DeltaOp(memberIndex, kind, -1, null, null, ReadNested(ref sr));
                default:
                    return new DeltaOp(memberIndex, kind, -1, null, null, null);
            }
        }

        private static int ReadIndex(ref SpanReader reader)
        {
            var index = reader.ReadVarUInt();
            if (index > int.MaxValue) throw new InvalidOperationException("sequence index out of Int32 range.");
            return (int)index;
        }

        public DeltaDocument ReadDocument()
        {
            var sr = new SpanReader(_data);

            if (_opt.IncludeHeader)
            {
                if (sr.ReadUInt32() != HeaderMagic)
                    throw new InvalidOperationException("Invalid BinaryDelta header magic.");

                var version = sr.ReadVarUInt();
                if (version != 1)
                    throw new NotSupportedException($"Unsupported BinaryDelta version {version}.");

                var fingerprint = sr.ReadVarUInt();
                if (_opt.StableTypeFingerprint != 0 && _opt.StableTypeFingerprint != fingerprint)
                    throw new InvalidOperationException("BinaryDelta schema fingerprint mismatch.");
                var flags = sr.ReadByte();
                if ((flags & ~0b11) != 0) throw new InvalidOperationException("Unknown BinaryDelta header flags.");
                var hasStrings = (flags & 0b01) != 0;
                var hasTypes = (flags & 0b10) != 0;

                if (hasStrings)
                {
                    var n = (int)sr.ReadVarUIntChecked(_opt.Safety.MaxOps);
                    sr.RequireBytes(n);
                    _strings = new string[n];
                    for (var i = 0; i < n; i++)
                        _strings[i] = sr.ReadUtf8StringInlineChecked(_opt.Safety.MaxStringBytes);
                }

                if (hasTypes)
                {
                    var n = (int)sr.ReadVarUIntChecked(_opt.Safety.MaxOps);
                    sr.RequireBytes((long)n * 18);
                    _enumTypes = new Type[n];
                    for (var i = 0; i < n; i++)
                        _enumTypes[i] = ReadTypeEntry(ref sr);
                }
            }

            var document = ReadOperations(ref sr);
            if (!sr.Remaining.IsEmpty) throw new InvalidOperationException("Trailing bytes after the delta document.");
            return document;
        }

        private DeltaDocument ReadOperations(ref SpanReader reader)
        {
            var count = (int)reader.ReadVarUIntChecked(_opt.Safety.MaxOps);
            reader.RequireBytes((long)count * 2);

            var document = new DeltaDocument();
            document.Ops.Capacity = count;
            CollectionsMarshal.SetCount(document.Ops, count);
            var operations = CollectionsMarshal.AsSpan(document.Ops);
            for (var index = 0; index < operations.Length; index++)
                operations[index] = ReadOp(ref reader);

            return document;
        }

        private DeltaDocument ReadNested(ref SpanReader reader)
        {
            if (++_nesting > _opt.Safety.MaxNesting)
                throw new InvalidOperationException("Max nesting exceeded");

            var document = ReadOperations(ref reader);
            _nesting--;
            return document;
        }

        private object ReadCollection(ref SpanReader reader, VTag tag)
        {
            if (++_nesting > _opt.Safety.MaxNesting)
                throw new InvalidOperationException("Max nesting exceeded.");
            var collection = tag switch
            {
                VTag.Array => ReadArray(ref reader),
                VTag.List => ReadList(ref reader),
                _ => ReadDict(ref reader)
            };
            _nesting--;
            return collection;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private object? ReadValue(ref SpanReader sr)
        {
            var tag = (VTag)sr.ReadByte();
            if (tag == VTag.Int32) return (int)sr.ReadVarInt();
            if (tag == VTag.StringInline) return sr.ReadUtf8StringInlineChecked(_opt.Safety.MaxStringBytes);
            return ReadOtherValue(ref sr, tag);
        }

        private object? ReadOtherValue(ref SpanReader sr, VTag tag)
        {
            switch (tag)
            {
                case VTag.Array:
                case VTag.List:
                case VTag.Dictionary:
                    return ReadCollection(ref sr, tag);
                case VTag.Null: return null;
                case VTag.BoolFalse: return false;
                case VTag.BoolTrue: return true;

                case VTag.SByte: return (sbyte)sr.ReadVarInt();
                case VTag.Byte: return (byte)sr.ReadVarUInt();
                case VTag.Int16: return (short)sr.ReadVarInt();
                case VTag.UInt16: return (ushort)sr.ReadVarUInt();
                case VTag.UInt32: return (uint)sr.ReadVarUInt();
                case VTag.Int64: return sr.ReadVarInt();
                case VTag.UInt64: return sr.ReadVarUInt();
                case VTag.Char16: return (char)sr.ReadUInt16();

                case VTag.Single:
                {
                    var u = sr.ReadUInt32();
                    return BitConverter.Int32BitsToSingle((int)u);
                }
                case VTag.Double:
                {
                    var u = sr.ReadUInt64();
                    return BitConverter.Int64BitsToDouble((long)u);
                }
                case VTag.Decimal: return ReadDecimal(ref sr);

                case VTag.StringRef:
                {
                    if (_strings is not { Length: > 0 }) throw new InvalidOperationException("No string table in stream.");
                    return _strings[sr.ReadVarUIntChecked(_strings.Length - 1)];
                }

                case VTag.Guid16:
                    return ReadGuid16(ref sr);

                case VTag.DateTimeBin64:
                    return DateTime.FromBinary(sr.ReadVarInt());

                case VTag.TimeSpanTicks:
                    return new TimeSpan(sr.ReadVarInt());

                case VTag.DateTimeOffset:
                {
                    var ticks = sr.ReadVarInt();
                    var offMin = (int)sr.ReadVarInt();
                    return new DateTimeOffset(ticks, TimeSpan.FromMinutes(offMin));
                }

                case VTag.Enum:
                {
                    var (enumT, underlying) = ReadEnumTypeIdentity(ref sr);
                    return ReadEnumValue(ref sr, enumT, underlying);
                }

                case VTag.ByteArray:
                {
                    var len = (int)sr.ReadVarUIntChecked(_opt.Safety.MaxStringBytes);
                    sr.RequireBytes(len);
                    var bytes = new byte[len];
                    sr.ReadBytes(bytes);
                    return bytes;
                }

                default:
                    throw new InvalidOperationException($"Unknown value tag: {tag}");
            }
        }

        private static decimal ReadDecimal(ref SpanReader sr)
        {
            var a = (int)sr.ReadUInt32();
            var b = (int)sr.ReadUInt32();
            var c = (int)sr.ReadUInt32();
            var d = (int)sr.ReadUInt32();
            return new decimal(new[] { a, b, c, d });
        }

        private Array ReadArray(ref SpanReader sr)
        {
            var elem = ReadTypeSpec(ref sr);
            var len = (int)sr.ReadVarUIntChecked(_opt.Safety.MaxOps);
            sr.RequireBytes(len);

            if (TryCreateArray(elem, len, out var arr, out var objecty))
            {
                if (objecty)
                    for (var i = 0; i < len; i++)
                        arr.SetValue(ReadValue(ref sr), i);
                else
                    for (var i = 0; i < len; i++)
                        arr.SetValue(ConvertValueForElement(ReadValue(ref sr), elem), i);

                return arr;
            }

            throw new NotSupportedException($"BinaryDelta cannot construct an array of '{elem}' in this runtime.");
        }

        private object ReadList(ref SpanReader sr)
        {
            var elem = ReadTypeSpec(ref sr);
            var len = (int)sr.ReadVarUIntChecked(_opt.Safety.MaxOps);
            sr.RequireBytes(len);
            var list = CreateList(elem, len);
            var il = (IList)list;
            for (var i = 0; i < len; i++) il.Add(ConvertValueForElement(ReadValue(ref sr), elem));

            return list;
        }

        private object ReadDict(ref SpanReader sr)
        {
            var keyT = ReadTypeSpec(ref sr);
            var valT = ReadTypeSpec(ref sr);
            var len = (int)sr.ReadVarUIntChecked(_opt.Safety.MaxOps);
            sr.RequireBytes((long)len * 2);
            var dict = CreateDictionary(keyT, valT, len);
            var id = (IDictionary)dict;
            for (var i = 0; i < len; i++)
            {
                var k = ConvertValueForElement(ReadValue(ref sr), keyT);
                var v = ConvertValueForElement(ReadValue(ref sr), valT);
                if (k is null || id.Contains(k))
                    throw new InvalidOperationException("BinaryDelta dictionary contains a null or duplicate key.");
                id.Add(k, v);
            }

            return dict;
        }

        private static bool TryCreateArray(Type t, int len, out Array arr, out bool isObject)
        {
            if (t == typeof(object))
            {
                arr = new object?[len];
                isObject = true;
                return true;
            }

            try
            {
                arr = Array.CreateInstance(t, len);
                isObject = false;
                return true;
            }
            catch
            {
                arr = Array.Empty<object>();
                isObject = true;
                return false;
            }
        }

        private static object CreateList(Type t, int capacity)
        {
            if (t == typeof(object)) return new List<object?>(capacity);

            try
            {
                return Activator.CreateInstance(typeof(List<>).MakeGenericType(t), capacity)!;
            }
            catch (Exception error) when (error is ArgumentException or MissingMethodException or NotSupportedException)
            {
                throw new NotSupportedException($"BinaryDelta cannot construct a list of '{t}' in this runtime.", error);
            }
        }

        private static object CreateDictionary(Type kt, Type vt, int capacity)
        {
            try
            {
                return Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(kt, vt), capacity)!;
            }
            catch (Exception error) when (error is ArgumentException or MissingMethodException or NotSupportedException)
            {
                throw new NotSupportedException($"BinaryDelta cannot construct a dictionary of '{kt}' and '{vt}' in this runtime.", error);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static object? ConvertValueForElement(object? val, Type target)
        {
            if (val is null ? !target.IsValueType : target.IsInstanceOfType(val)) return val;

            throw new InvalidOperationException($"BinaryDelta value is incompatible with '{target}'.");
        }

        private (Type enumType, Type underlying) ReadEnumTypeIdentity(ref SpanReader sr)
        {
            var kind = (TypeSpecKind)sr.ReadVarUInt();
            switch (kind)
            {
                case TypeSpecKind.Enum:
                    if (_opt.IncludeHeader && _enumTypes is { Length: > 0 })
                    {
                        var tid = (int)sr.ReadVarUIntChecked(_enumTypes.Length - 1);
                        var t = _enumTypes[tid];
                        return (t, Enum.GetUnderlyingType(t));
                    }
                    else
                    {
                        var full = sr.ReadUtf8StringInlineChecked(_opt.Safety.MaxStringBytes);
                        var asm = sr.ReadUtf8StringInlineChecked(_opt.Safety.MaxStringBytes);
                        var mvid = ReadGuid16(ref sr);

                        var t = ResolveEnumType(full, asm, mvid);
                        return (t, Enum.GetUnderlyingType(t));
                    }
                default:
                    throw new InvalidOperationException("Bad enum type identity.");
            }
        }

        private static Type ResolveEnumType(string fullName, string assembly, Guid identity) =>
            ResolveType(fullName, assembly, identity) is { IsEnum: true } type
                ? type
                : throw new TypeLoadException($"Enum '{fullName}' was not found in '{assembly}' ({identity}).");

        private static Type? ResolveType(string full, string asmSimple, Guid mvid)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var n = asm.GetName();
                if (!string.Equals(n.Name, asmSimple, StringComparison.Ordinal)) continue;

                try
                {
                    if (asm.ManifestModule.ModuleVersionId == mvid) return asm.GetType(full, false, false);
                }
                catch
                {
                }
            }

            return null;
        }

        private Type ReadTypeSpec(ref SpanReader sr)
        {
            var kind = (TypeSpecKind)sr.ReadVarUInt();
            switch (kind)
            {
                case TypeSpecKind.Object: return typeof(object);
                case TypeSpecKind.PrimitiveOrKnown:
                    return ReadKnownType(ref sr);
                case TypeSpecKind.Enum:
                    if (_opt.IncludeHeader && _enumTypes is { Length: > 0 })
                    {
                        var tid = (int)sr.ReadVarUIntChecked(_enumTypes.Length - 1);
                        return _enumTypes[tid];
                    }

                    var full = sr.ReadUtf8StringInlineChecked(_opt.Safety.MaxStringBytes);
                    var asm = sr.ReadUtf8StringInlineChecked(_opt.Safety.MaxStringBytes);

                    var mvid = ReadGuid16(ref sr);

                    return ResolveEnumType(full, asm, mvid);
                default:
                    throw new InvalidOperationException("Unknown TypeSpecKind.");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static object ReadEnumValue(ref SpanReader sr, Type enumType, Type underlying)
        {
            if (underlying == typeof(sbyte))
            {
                var v = (sbyte)sr.ReadVarInt();
                return Enum.ToObject(enumType, v);
            }

            if (underlying == typeof(byte))
            {
                var v = (byte)sr.ReadVarUInt();
                return Enum.ToObject(enumType, v);
            }

            if (underlying == typeof(short))
            {
                var v = (short)sr.ReadVarInt();
                return Enum.ToObject(enumType, v);
            }

            if (underlying == typeof(ushort))
            {
                var v = (ushort)sr.ReadVarUInt();
                return Enum.ToObject(enumType, v);
            }

            if (underlying == typeof(int))
            {
                var v = (int)sr.ReadVarInt();
                return Enum.ToObject(enumType, v);
            }

            if (underlying == typeof(uint))
            {
                var v = (uint)sr.ReadVarUInt();
                return Enum.ToObject(enumType, v);
            }

            if (underlying == typeof(long))
            {
                var v = sr.ReadVarInt();
                return Enum.ToObject(enumType, v);
            }

            if (underlying == typeof(ulong))
            {
                var v = sr.ReadVarUInt();
                return Enum.ToObject(enumType, v);
            }

            throw new NotSupportedException("Unsupported enum underlying type.");
        }

        private static Type ReadKnownType(ref SpanReader sr)
        {
            var code = (KnownTypeCode)sr.ReadVarUInt();
            return code switch
            {
                KnownTypeCode.SByte => typeof(sbyte),
                KnownTypeCode.Byte => typeof(byte),
                KnownTypeCode.Int16 => typeof(short),
                KnownTypeCode.UInt16 => typeof(ushort),
                KnownTypeCode.Int32 => typeof(int),
                KnownTypeCode.UInt32 => typeof(uint),
                KnownTypeCode.Int64 => typeof(long),
                KnownTypeCode.UInt64 => typeof(ulong),
                KnownTypeCode.Single => typeof(float),
                KnownTypeCode.Double => typeof(double),
                KnownTypeCode.Decimal => typeof(decimal),
                KnownTypeCode.Bool => typeof(bool),
                KnownTypeCode.Char => typeof(char),
                KnownTypeCode.String => typeof(string),
                KnownTypeCode.Guid => typeof(Guid),
                KnownTypeCode.DateTime => typeof(DateTime),
                KnownTypeCode.TimeSpan => typeof(TimeSpan),
                KnownTypeCode.DateTimeOffset => typeof(DateTimeOffset),
                _ => throw new InvalidOperationException("Unknown BinaryDelta type code.")
            };
        }

        private Type ReadTypeEntry(ref SpanReader sr)
        {
            var full = sr.ReadUtf8StringInlineChecked(_opt.Safety.MaxStringBytes);
            var asm = sr.ReadUtf8StringInlineChecked(_opt.Safety.MaxStringBytes);

            var mvid = ReadGuid16(ref sr);

            return ResolveType(full, asm, mvid) ??
                   throw new TypeLoadException($"Type '{full}' not found in '{asm}' ({mvid}).");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Guid ReadGuid16(ref SpanReader sr)
        {
            Span<byte> tmp = stackalloc byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(tmp, sr.ReadUInt64());
            BinaryPrimitives.WriteUInt64LittleEndian(tmp.Slice(8), sr.ReadUInt64());
            return new Guid(tmp);
        }
    }


    private ref struct SpanWriter(IBufferWriter<byte> bw)
    {
        private Span<byte> _span = bw.GetSpan(256);
        private int _pos = 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteByte(byte b)
        {
            if (_pos >= _span.Length) Grow(1);

            _span[_pos++] = b;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteBytes(ReadOnlySpan<byte> src)
        {
            var needed = src.Length;
            if (_pos + needed > _span.Length) Grow(needed);

            src.CopyTo(_span.Slice(_pos));
            _pos += needed;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteUInt16(ushort v)
        {
            if (_pos + 2 > _span.Length) Grow(2);

            BinaryPrimitives.WriteUInt16LittleEndian(_span.Slice(_pos), v);
            _pos += 2;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteUInt32(uint v)
        {
            if (_pos + 4 > _span.Length) Grow(4);

            BinaryPrimitives.WriteUInt32LittleEndian(_span.Slice(_pos), v);
            _pos += 4;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteUInt64(ulong v)
        {
            if (_pos + 8 > _span.Length) Grow(8);

            BinaryPrimitives.WriteUInt64LittleEndian(_span.Slice(_pos), v);
            _pos += 8;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteVarUInt(ulong v)
        {
            while (v >= 0x80)
            {
                WriteByte((byte)(v | 0x80));
                v >>= 7;
            }

            WriteByte((byte)v);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteVarUInt(uint v)
        {
            WriteVarUInt((ulong)v);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteVarInt(long value)
        {
            var zigzag = (ulong)((value << 1) ^ (value >> 63));
            WriteVarUInt(zigzag);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteVarInt(int value)
        {
            WriteVarInt((long)value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteUtf8StringInline(string s) => WriteUtf8StringInline(s, Utf8.GetByteCount(s));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteUtf8StringInline(string s, int byteCount)
        {
            WriteVarUInt((uint)byteCount);
            if (_pos + byteCount > _span.Length) Grow(byteCount);

            _ = Utf8.GetBytes(s.AsSpan(), _span.Slice(_pos));
            _pos += byteCount;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Grow(int needed)
        {
            bw.Advance(_pos);
            _span = bw.GetSpan(Math.Max(needed, _span.Length * 2));
            _pos = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Commit()
        {
            bw.Advance(_pos);
            _pos = 0;
        }
    }

    private ref struct SpanReader(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> _data = data;

        public ReadOnlySpan<byte> Remaining => _data;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte ReadByte()
        {
            RequireBytes(1);
            var b = _data[0];
            _data = _data.Slice(1);
            return b;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReadBytes(Span<byte> dst)
        {
            RequireBytes(dst.Length);
            _data.Slice(0, dst.Length).CopyTo(dst);
            _data = _data.Slice(dst.Length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ushort ReadUInt16()
        {
            RequireBytes(2);
            var v = BinaryPrimitives.ReadUInt16LittleEndian(_data);
            _data = _data.Slice(2);
            return v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint ReadUInt32()
        {
            RequireBytes(4);
            var v = BinaryPrimitives.ReadUInt32LittleEndian(_data);
            _data = _data.Slice(4);
            return v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong ReadUInt64()
        {
            RequireBytes(8);
            var v = BinaryPrimitives.ReadUInt64LittleEndian(_data);
            _data = _data.Slice(8);
            return v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReadOperationPrefix(out DeltaKind kind, out int memberIndex)
        {
            RequireBytes(2);
            var prefix = BinaryPrimitives.ReadUInt16LittleEndian(_data);
            // Both fields are single-byte varints unless either continuation bit is set.
            if ((prefix & 0x8080) != 0)
            {
                ReadOperationPrefixSlow(out kind, out memberIndex);
                return;
            }

            kind = (DeltaKind)(prefix & 0xff);
            var member = prefix >> 8;
            memberIndex = (member >> 1) ^ -(member & 1);
            _data = _data.Slice(2);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ReadOperationPrefixSlow(out DeltaKind kind, out int memberIndex)
        {
            kind = (DeltaKind)ReadVarUInt();
            var member = ReadVarInt();
            if (member is < int.MinValue or > int.MaxValue)
                throw new InvalidOperationException("memberIndex out of Int32 range.");
            memberIndex = (int)member;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong ReadVarUInt()
        {
            var first = ReadByte();
            return first < 0x80 ? first : ReadVarUIntSlow(first);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private ulong ReadVarUIntSlow(byte first)
        {
            var result = (ulong)(first & 0x7f);
            var shift = 7;
            while (true)
            {
                var b = ReadByte();
                if (shift == 63 && b > 1) throw new InvalidOperationException("VarUInt out of range.");
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;

                shift += 7;
                if (shift >= 64)
                    throw new InvalidOperationException("VarUInt too long.");
            }

            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long ReadVarInt()
        {
            var u = ReadVarUInt();
            var val = (long)((u >> 1) ^ (~(u & 1) + 1));
            return val;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint ReadVarUIntChecked(int max)
        {
            var v = ReadVarUInt();
            if (v > (ulong)max) throw new InvalidOperationException("Bound exceeded.");

            return (uint)v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string ReadUtf8StringInlineChecked(int maxLen)
        {
            var len = (int)ReadVarUIntChecked(maxLen);
            RequireBytes(len);
            var bytes = _data.Slice(0, len);
            var s = Utf8.GetString(bytes);
            _data = _data.Slice(len);
            return s;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RequireBytes(long n)
        {
            if (_data.Length < n) throw new InvalidOperationException("Unexpected EOF");
        }
    }
}
