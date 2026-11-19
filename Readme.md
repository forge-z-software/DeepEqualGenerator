# DeepEqualGenerator

Compare .NET models with generated code. Ask for a boolean in the hot path or get the first mismatch with its path and both values.

## Quick start

Version 2.0 bundles the generator, attributes, and runtime in one package. It targets .NET 8 or later and requires a C# 12 compiler with Roslyn 4.11 or later.

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

## Diffs and deltas

Set `GenerateDiff = true` to enable `GetDeepDiff`. Set `GenerateDelta = true` to enable `ComputeDeepDelta` and `ApplyDeepDelta`. The generated patch can update writable members and supported mutable collections in place. It uses a replacement for immutable state and for list changes where positional operations would be ambiguous. Capture the returned object when applying a patch to a class.

```csharp
var delta = original.ComputeDeepDelta(desired);
var result = original.ApplyDeepDelta(delta);
bool reproduced = desired.AreDeepEqual(result);
```

Apply a delta to the same baseline and model schema used to compute it. In-memory replacements can retain references to the desired values. The in-memory API checks the root type of generated delta documents before applying them.

Use `BinaryDeltaCodec` to transport supported delta values between matching schemas. Set `IncludeHeader = true` and give both endpoints the same nonzero `StableTypeFingerprint`. Change that identifier when you change the schema or comparison rules. The codec supports primitives, strings, GUIDs, date and time values, enums, byte arrays, and supported one-dimensional arrays, lists, and dictionaries. Arbitrary model instances and cycles require an application-level representation. Dictionary containers decode with the default key comparer. Enum identity requires the matching enum assembly. [Binary delta round-trip example](samples/DeltaRoundTrip/Program.cs)

Generated diff and delta extensions require a nongeneric root. Equality and mismatch explanations also support generic roots and Native AOT. The binary codec uses runtime type construction for typed collections, so its supported deployment target is the regular .NET runtime.

## Dirty bits and access tracking

Add `[DeltaTrack]` to a partial model with delta generation enabled and call the generated `__MarkDirty(__Bit_PropertyName)` from the property setter. The delta generator can then use the marks to find changed members. Set `ComparisonOptions.ValidateDirtyOnEmit` to check a flagged value against the baseline before emitting an operation.

Set `AccessTrack = AccessMode.Write` on `DeltaTrack` to collect write diagnostics. Choose bits, counts, or counts with the last write time through `AccessGranularity`. The generated `__DumpAccessJson()` method exposes the tracking data.

## Build and verify

Use a serviced .NET 8.0.4xx SDK. Build, test, pack, and run both examples against the local feed.

```sh
dotnet build DeepEqual.Generator.sln -c Release
dotnet test DeepEqual.Generator.Tests/DeepEqual.Generator.Tests.csproj -c Release --no-build
dotnet pack DeepEqual.Generator.Shared/DeepEqual.Generator.Shared.csproj -c Release --no-build -o artifacts/packages
dotnet restore samples/OrderComparison/OrderComparison.csproj --source artifacts/packages --packages artifacts/smoke-cache
dotnet run --project samples/OrderComparison/OrderComparison.csproj -c Release --no-restore
dotnet restore samples/DeltaRoundTrip/DeltaRoundTrip.csproj --source artifacts/packages --packages artifacts/smoke-cache
dotnet run --project samples/DeltaRoundTrip/DeltaRoundTrip.csproj -c Release --no-restore
```

Install 2.0.1 from that local feed with `dotnet add package DeepEqual.Generator --version 2.0.1 --source <feed-path>`. For a 1.x project, remove its separate `DeepEqual.Generator.Shared` package reference and call `expected.AreDeepEqual(actual)` with `using DeepEqual;`.

Run the [comparison benchmarks](DeepEqual.Generator.Benchmarking/DeepEqualityBenchmarks.cs) with `dotnet run -c Release --project DeepEqual.Generator.Benchmarking -- --filter '*DeepEqualityBenchmarks*'`. The fixture checks both comparers and the mismatch path before timing equal models and a late mismatch.

Run the [feature benchmarks](DeepEqual.Generator.Benchmarking/FeaturePerformanceBenchmarks.cs) with `dotnet run -c Release --project DeepEqual.Generator.Benchmarking -- --filter '*FeaturePerformanceBenchmarks*'`. They cover arrays, dictionaries, unordered and keyed collections, polymorphism, cycles, mismatch paths, structured diffs, delta computation and application, the binary codec, and tracking. The benchmarks time changed and unchanged models, patch application on its own and with a fresh target, and binary encoding with and without tables.
