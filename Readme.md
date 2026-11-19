# DeepEqualGenerator

Compare .NET models with generated code. Ask for a boolean in the hot path or get the first mismatch with its path and both values.

## Quick start

The 2.0 preview bundles the generator, attributes, and runtime in one package. It targets .NET 8 or later and requires a C# 12 compiler with Roslyn 4.11 or later.

```csharp
using DeepEqual;
using DeepEqual.Generator.Shared;

var expected = new Order { Lines = [new("SKU-8", 1), new("SKU-9", 2)] };
var actual = new Order { Lines = [new("SKU-9", 3), new("SKU-8", 1)] };

bool equal = expected.AreDeepEqual(actual);
DeepMismatch? mismatch = expected.GetDeepMismatch(actual);
if (mismatch is { } change)
    Console.WriteLine($"{change.Path} changed from {change.Expected} to {change.Actual}");

[DeepComparable]
public sealed class Order
{
    [DeepCompare(KeyMembers = [nameof(Line.Sku)])]
    public List<Line> Lines { get; init; } = [];
}

public sealed record Line(string Sku, int Qty);
```

```text
$.Lines["SKU-9"].Qty changed from 2 to 3
```

`GetDeepMismatch` runs the same generated comparison as `AreDeepEqual` and returns `null` for equal inputs. It reports the first failure in traversal order. Paths start at `$` and include property names, zero-based sequence indices, and dictionary or matching keys where available. A count or shape mismatch is reported at the collection. If unordered elements have no unique matching key, the mismatch can be reported at the collection or key bucket.

## Comparison rules

- Readable public instance properties and fields are included by default, including inherited members. Use `[DeepComparable(IncludeBaseMembers = false)]` to exclude inherited members or `IncludeInternals = true` to include eligible internal members. Getters run during comparison, so compare stable snapshots when values can mutate.
- Cycle tracking is enabled by default and treats repeated pairs on the current path as equal. Shared reference topology is ignored. Set `CycleTracking = false` only for acyclic graphs.
- Lists and one-dimensional arrays are ordered by default. `OrderInsensitive = true` matches elements one-to-one and counts duplicates. `KeyMembers` enables unordered matching within key buckets. Prefer unique keys for precise mismatch paths and larger collections.
- Dictionary keys and matching keys use .NET default key equality regardless of the input dictionaries' comparers. `ComparisonOptions.StringComparison` governs the compared string values. Date and time values compare `DateTime.Kind` or `DateTimeOffset.Offset` as well as the time value.
- `ComparisonOptions` supports absolute numeric tolerances and NaN handling. A tolerance can relate values non-transitively and is intended for comparisons, assertions, or validation.
- `[DeepCompare(Kind = CompareKind.Skip)]` skips a member. `Shallow` uses its regular equality and `Reference` requires the same instance. Use `ComparerType` for an `IEqualityComparer<T>` with an accessible parameterless constructor. Apply `Members` or `IgnoreMembers` to the type that declares those members.
- For values stored as `object` or an interface, generated helpers are used when the runtime type has been registered. Known runtime containers are compared by content. Other runtime values fall back to their regular equality. Use the typed extensions for generic comparison roots.

Compare an external model by adding `[assembly: ExternalDeepComparable(typeof(ExternalModel))]`. `[assembly: ExternalDeepCompare(typeof(ExternalModel), nameof(ExternalModel.Timestamp), Kind = CompareKind.Skip)]` applies a rule to a direct member.

## Build and try the preview

The repository uses the .NET 8.0.4xx SDK. Pack locally and run the consumer example against that package.

```sh
dotnet test DeepEqual.Generator.Tests/DeepEqual.Generator.Tests.csproj -c Release
dotnet pack DeepEqual.Generator.Shared/DeepEqual.Generator.Shared.csproj -c Release -o artifacts/packages
dotnet restore samples/OrderComparison/OrderComparison.csproj --source artifacts/packages --packages artifacts/smoke-cache
dotnet run --project samples/OrderComparison/OrderComparison.csproj -c Release --no-restore
```

Install the preview from that local feed with `dotnet add package DeepEqual.Generator --version 2.0.0-preview.1 --source <feed-path>`.

Run the [comparison benchmarks](DeepEqual.Generator.Benchmarking/DeepEqualityBenchmarks.cs) with `dotnet run -c Release --project DeepEqual.Generator.Benchmarking -- --filter '*DeepEqualityBenchmarks*'`. The fixture checks both comparers and the mismatch path before timing equal models and a late mismatch.

## Experimental change APIs

`GenerateDiff = true` enables the structured `GetDeepDiff` API. `GenerateDelta = true` enables `ComputeDeepDelta` and `ApplyDeepDelta`. Capture the returned target when applying a patch to a class, since the patch can replace the instance. Change tracking and the binary delta codec are also experimental. Use deltas with known matching schemas and test compute-and-apply round trips on your models before depending on them.
