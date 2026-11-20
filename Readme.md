# DeepEqualGenerator

Compare .NET model snapshots with generated code. See what changed by property, index, or business key. Create a patch and apply it with the same comparison rules.

## Quick start

DeepEqualGenerator 3.0 targets .NET 11. Install the .NET 11 SDK to build and run the samples. One NuGet package contains the generator, attributes, runtime, and IDE fixes.

Build the 3.0.0 package using the commands in [Build and verify](#build-and-verify), then install it into a .NET 11 project from that local feed.

```sh
dotnet add package DeepEqual.Generator --version 3.0.0 --source "/path/to/DeepEqualGenerator/artifacts/packages"
```

```csharp
using DeepEqual;
using DeepEqual.Generator.Shared;

var before = new Inventory { Lines = [new("SKU-9", 2), new("SKU-11", 1)] };
var after = new Inventory { Lines = [new("SKU-9", 3), new("SKU-12", 1)] };

Console.WriteLine(before.DescribeDeepChanges(after).ToText());
var patched = before.ApplyDeepDelta(before.ComputeDeepDelta(after));
after.AssertDeepEqual(patched);

[DeepComparable(GenerateDelta = true)]
public sealed class Inventory
{
    [DeepCompare(KeyMembers = [nameof(Line.Sku)])]
    public List<Line> Lines { get; set; } = [];
}

public sealed record Line(string Sku, int Quantity);
```

```text
$.Lines["SKU-9"].Quantity 2 → 3
$.Lines["SKU-11"] removed <Line>
$.Lines["SKU-12"] added <Line>
```

| When you need | Use |
| --- | --- |
| A fast yes or no | `AreDeepEqual` |
| The first reason two objects differ | `GetDeepMismatch` |
| A bounded change report with named paths | `DescribeDeepChanges` |
| A test assertion with that same report | `AssertDeepEqual` |
| A side-by-side HTML report or CI artifact | [`ToHtml`](docs/HtmlReport.md) |
| The generated rules for a model | `ExplainDeepModel` |
| A structural diff for code that consumes operation IDs | `GetDeepDiff` with `GenerateDiff = true` |
| An in-memory patch | `ComputeDeepDelta` and `ApplyDeepDelta` with `GenerateDelta = true` |
| A checked, revisioned patch between processes | `CreateDeepSync` with `GenerateDelta = true` and JSON serializer metadata |

[Run the inventory walkthrough](samples/DeltaRoundTrip) · [Read the comparison recipes](docs/Recipes.md) · [Troubleshoot a compiler diagnostic](docs/Diagnostics.md)

Save a failed assertion as a filterable HTML report with the same redaction as its test output. The [GitHub Actions example](docs/HtmlReport.md) shows how to upload the report on failure.

## Comparison rules

- Public readable instance properties and fields are included by default. Use `IncludeBaseMembers = false` to exclude inherited members or `IncludeInternals = true` for eligible internal members. Getters run during comparison. Use stable snapshots when values can mutate concurrently.
- Cycles are tracked by active reference pairs. Shared reference topology is ignored. Set `CycleTracking = false` for acyclic models.
- Ordered collections are compared by position. `OrderInsensitive = true` matches duplicates one-to-one. `KeyMembers` matches by key and ignores order by default. Set `OrderInsensitive = false` alongside keys to compare order and report position changes. Reports compare an unordered bucket as a group when items cannot be paired unambiguously.
- Dictionary and matching keys use default .NET key equality. String options apply to compared values. A dictionary patch can use a replacement when the receiver’s comparer cannot represent the desired keys. Date values compare `DateTime.Kind` or `DateTimeOffset.Offset` as well as the time value.
- `ComparisonOptions` controls numeric tolerances, NaN, and string comparison. Named `ComparisonProfile` instances share options between assertions, validation, and sync. A numeric tolerance may be non-transitive.
- `[DeepCompare(Kind = CompareKind.Skip)]` excludes a member. `Shallow` uses ordinary equality and `Reference` uses reference identity. `ComparerType` accepts an `IEqualityComparer<T>` with an accessible parameterless constructor. Apply `Members` or `IgnoreMembers` to the declaring type.
- Generated helpers handle known runtime model types in interface and object members. Known runtime containers compare by content. Reports list where ordinary equality was used for an unregistered type.
- Use `[assembly: ExternalDeepComparable(typeof(ExternalModel))]` for types you cannot annotate. `ExternalDeepCompare` applies rules to direct members. Roots with external overrides use replacement diffs and deltas.

## Patching and deployment

| Feature | Supported workflow |
| --- | --- |
| Comparison, reports, assertions | Generated class and struct roots, including generic roots, on the regular runtime and Native AOT |
| In-memory diffs and deltas | Granular operations for supported writable members and mutable collections. Immutable and open generic roots use replacement when needed. |
| BDC1 binary codec | Primitive values, strings, known time and ID types, enums, byte arrays, and supported containers. Explicit `JsonTypeInfo` registrations extend value support. |
| Checked sync | Generated schemas, revision checks, isolated preview and apply, packet inspection, and full-snapshot recovery. Source-generated serializer metadata supports Native AOT. Register typed container and enum values for AOT. |

Raw `ApplyDeepDelta` can mutate the target. Capture its return value for classes because a patch can replace the entire object. Apply raw deltas to the same model and baseline used to compute them. Use [checked sync](docs/Sync.md) to validate a packet and stage the result independently.

To record dirty members, add `[DeltaTrack]` to a partial model with delta generation enabled and call the generated `__MarkDirty(__Bit_PropertyName)` from the setter. `ValidateDirtyOnEmit` checks flagged members before emitting an operation. Checked sync compares whole snapshots and leaves dirty markers alone. `AccessTrack = AccessMode.Write` records write diagnostics and `__DumpAccessJson()` exposes them.

## Build and verify

```sh
dotnet build DeepEqual.Generator.sln -c Release
dotnet test DeepEqual.Generator.Tests -c Release --no-build
dotnet pack DeepEqual.Generator.Shared -c Release --no-build -o artifacts/packages
dotnet restore samples/DeltaRoundTrip --source artifacts/packages --packages artifacts/smoke-cache
dotnet run --project samples/DeltaRoundTrip -c Release --no-restore
```

The [benchmark guide](docs/Benchmarks.md) covers comparisons, reports, patches, and full snapshots. For a local package, add `--source <feed-path>` to the installation command. For a 1.x project, remove the separate `DeepEqual.Generator.Shared` package reference and call `expected.AreDeepEqual(actual)` with `using DeepEqual;`.
