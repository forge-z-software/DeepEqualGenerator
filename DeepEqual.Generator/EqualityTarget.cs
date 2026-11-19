using Microsoft.CodeAnalysis;
using System.Collections.Generic;

namespace DeepEqual.Generator;

internal readonly record struct EqualityTarget(
    INamedTypeSymbol Type,
    bool IncludeInternals,
    bool OrderInsensitiveCollections,
    bool CycleTrackingEnabled,
    bool IncludeBaseMembers,
    IReadOnlyDictionary<ISymbol, AttributeData>? ExternalRules = null)
{
    public AttributeData? GetMemberRule(ISymbol member)
    {
        if (ExternalRules is not null && ExternalRules.TryGetValue(member, out var rule))
            return rule;

        return GenCommon.GetDeepCompareAttribute(member);
    }
}