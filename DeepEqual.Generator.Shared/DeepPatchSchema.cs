using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;
using DeepEqual.Generator.Shared;

namespace DeepEqual;

/// <summary>Generated type and member information used to check a patch before applying it.</summary>
public sealed class DeepPatchSchema(Type type, params DeepPatchMember[] members)
{
    internal Type Type { get; } = type;
    internal IReadOnlyDictionary<int, DeepPatchMember> Members { get; } =
        new Dictionary<int, DeepPatchMember>(Array.ConvertAll(members, member =>
            new KeyValuePair<int, DeepPatchMember>(member.Id, member)));
}

public sealed record DeepPatchMember(int Id, string Name, Type Type, Func<object, object?> Read,
    bool CanWrite, Type? ElementType = null, Type? KeyType = null, Type? DictionaryValueType = null,
    bool CanNest = false);

public sealed record DeepPatchIssue(string Path, string Message);

internal sealed class DeepPatchValidator
{
    private readonly Dictionary<Type, DeepPatchSchema> _schemas = new();

    internal DeepPatchValidator(DeepPatchSchema[] schemas)
    {
        foreach (var schema in schemas) _schemas.Add(schema.Type, schema);
    }

    internal DeepPatchIssue? Validate(Type root, object? baseline, DeltaDocument delta) =>
        Validate(root, baseline, delta, "$", 0);

    private DeepPatchIssue? Validate(Type declaredType, object? baseline, DeltaDocument delta, string path, int depth)
    {
        if (depth > 64) return new(path, "Patch nesting exceeds the supported limit.");
        if (delta.IsEmpty) return null;
        if (delta.Ops.Count == 1 && delta.Ops[0] is { Kind: DeltaKind.ReplaceObject, MemberIndex: -1 } replace)
            return Accepts(declaredType, replace.Value) ? null : new(path, "Replacement has an incompatible type.");
        if (baseline is null) return new(path, "Member operations require a baseline object.");
        if (!_schemas.TryGetValue(declaredType, out var schema))
            return new(path, "This value has no generated patch schema.");

        Dictionary<int, List<object?>>? collections = null;
        HashSet<(int Member, object Key)>? dictionaryOperations = null;
        HashSet<int>? replaced = null;
        HashSet<int>? edited = null;
        foreach (var operation in delta.Ops)
        {
            if (!schema.Members.TryGetValue(operation.MemberIndex, out var member))
                return new(path, $"Unknown member ID {operation.MemberIndex}.");
            var memberPath = path + "." + member.Name;
            if (replaced?.Contains(member.Id) == true) return new(memberPath, "A replacement must be the only operation on its member.");
            if (operation.Kind == DeltaKind.SetMember)
            {
                if (!member.CanWrite || !Accepts(member.Type, operation.Value) || edited?.Contains(member.Id) == true)
                    return new(memberPath, "Member cannot accept this replacement.");
                (replaced ??= []).Add(member.Id);
                continue;
            }

            (edited ??= []).Add(member.Id);
            var current = member.Read(baseline);
            if (operation.Kind == DeltaKind.NestedMember && member.CanNest && operation.Nested is { } nested &&
                current is not null)
            {
                var issue = Validate(current.GetType(), current, nested, memberPath, depth + 1);
                if (issue is not null) return issue;
                continue;
            }

            if (member.ElementType is { } element && operation.Kind is
                    DeltaKind.SeqAddAt or DeltaKind.SeqRemoveAt or DeltaKind.SeqReplaceAt or DeltaKind.SeqNestedAt)
            {
                var elementPath = $"{memberPath}[{operation.Index}]";
                if (!(collections ??= new()).TryGetValue(member.Id, out var list))
                {
                    if (current is not IList { IsReadOnly: false, IsFixedSize: false } slot)
                        return new(memberPath, "Sequence operations require a mutable list.");
                    list = new List<object?>(slot.Count);
                    foreach (var item in slot) list.Add(item);
                    collections.Add(member.Id, list);
                }

                var index = operation.Index;
                if (index < 0 || index > list.Count || index == list.Count && operation.Kind != DeltaKind.SeqAddAt)
                    return new(elementPath, "Sequence index is outside the current list.");
                switch (operation.Kind)
                {
                    case DeltaKind.SeqNestedAt:
                        if (operation.Nested is not { } sequenceDelta || list[index] is not { } sequenceItem)
                            return new(elementPath, "There is no list element to patch.");
                        var issue = Validate(sequenceItem.GetType(), sequenceItem, sequenceDelta, elementPath, depth + 1);
                        if (issue is not null) return issue;
                        break;
                    case DeltaKind.SeqRemoveAt:
                        if (operation.Value is null || !Equals(list[index], operation.Value))
                            return new(elementPath, "The expected list element has changed.");
                        list.RemoveAt(index);
                        break;
                    case DeltaKind.SeqAddAt:
                        if (!Accepts(element, operation.Value))
                            return new(elementPath, "The list element has an incompatible type.");
                        list.Insert(index, operation.Value);
                        break;
                    default:
                        if (!Accepts(element, operation.Value))
                            return new(elementPath, "The list element has an incompatible type.");
                        list[index] = operation.Value;
                        break;
                }
                continue;
            }

            if (member.KeyType is { } keyType && operation.Kind is
                    DeltaKind.DictSet or DeltaKind.DictRemove or DeltaKind.DictNested)
            {
                if (operation.Key is not { } key || !Accepts(keyType, key))
                    return new(memberPath, "The dictionary key has an incompatible type.");
                var keyPath = KeyPath(memberPath, key);
                if (current is not IDictionary { IsReadOnly: false, IsFixedSize: false } map)
                    return new(memberPath, "Dictionary operations require a mutable dictionary.");
                if (!(dictionaryOperations ??= []).Add((member.Id, key)))
                    return new(keyPath, "A dictionary key may have only one operation.");
                if (operation.Kind == DeltaKind.DictSet)
                {
                    if (!Accepts(member.DictionaryValueType!, operation.Value))
                        return new(keyPath, "The dictionary value has an incompatible type.");
                }
                else if (!map.Contains(key))
                {
                    return new(keyPath, "The expected dictionary key is missing.");
                }
                else if (operation.Kind == DeltaKind.DictNested)
                {
                    if (operation.Nested is not { } dictionaryDelta || map[key] is not { } dictionaryValue)
                        return new(keyPath, "There is no dictionary value to patch.");
                    var issue = Validate(dictionaryValue.GetType(), dictionaryValue, dictionaryDelta, keyPath, depth + 1);
                    if (issue is not null) return issue;
                }
                continue;
            }
            return new(memberPath, $"Operation {operation.Kind} is unsupported for this member.");
        }
        return null;
    }

    internal IEnumerable<(string Path, DeltaOp Operation)> Describe(Type type, DeltaDocument delta, string path = "$",
        int depth = 0)
    {
        if (depth > 64) yield break;
        _schemas.TryGetValue(type, out var schema);
        foreach (var operation in delta.Ops)
        {
            if (operation is { Kind: DeltaKind.ReplaceObject, MemberIndex: -1 })
            {
                yield return (path, operation);
                continue;
            }

            DeepPatchMember? member = null;
            schema?.Members.TryGetValue(operation.MemberIndex, out member);
            var memberPath = path + "." + (member?.Name ?? $"#{operation.MemberIndex}");
            if (operation.Index >= 0) memberPath += $"[{operation.Index}]";
            if (operation.Key is { } key) memberPath = KeyPath(memberPath, key);
            if (operation.Nested is { } nested)
            {
                var childType = member?.DictionaryValueType ?? member?.ElementType ?? member?.Type ?? typeof(object);
                foreach (var item in Describe(childType, nested, memberPath, depth + 1)) yield return item;
            }
            else
            {
                yield return (memberPath, operation);
            }
        }
    }

    private static bool Accepts(Type type, object? value) =>
        value is null ? !type.IsValueType || Nullable.GetUnderlyingType(type) is not null :
        type.IsInstanceOfType(value);

    private static string KeyPath(string path, object key) => key is string text
        ? path + "[\"" + JsonEncodedText.Encode(text) + "\"]"
        : path + "[" + DeepChangeReport.FormatValue(key, 120) + "]";
}
