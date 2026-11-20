# Benchmarks

## .NET 11 results (3.0.0)

This run used SDK 11.0.100, runtime 11.0.0, and BenchmarkDotNet 0.16.0-preview.2 on Windows 11 with an Intel Core i9-10900K. Each case ran in a separate benchmark process with five warmup and ten measurement iterations. Times are means.

| 128-line order | Handwritten comparison | Generated comparison | First mismatch report |
| --- | ---: | ---: | ---: |
| Equal | 613 ns | 457 ns | 1.68 µs |
| Change on line 0 | 21.3 ns | 42.5 ns | 182 ns |
| Change on line 127 | 511 ns | 466 ns | 1.82 µs |

Both Boolean comparers allocated zero bytes in all three cases.

| Operation | Mean | Allocated |
| --- | ---: | ---: |
| Match 128 reordered items by key | 14.85 µs | 24,272 B |
| Compute a structured diff | 1.93 µs | 1,160 B |
| Apply an existing delta | 135 ns | 0 B |
| Encode a BDC1 delta | 834 ns | 1,520 B |
| Decode a BDC1 delta | 439 ns | 920 B |

The checked sync fixture changes eight dictionary entries. Timings exclude network and disk I/O.

| Inventory entries | Full change report | Create verified patch | Receive verified patch |
| ---: | ---: | ---: | ---: |
| 128 | 7.53 µs | 90.9 µs | 73.7 µs |
| 4,096 | 204 µs | 3.45 ms | 3.80 ms |

The historical measurements used different benchmark settings. To isolate the effect of a runtime upgrade, rerun both versions with matching settings and workloads.

## Historical .NET 8 results

The release measurements below used .NET 8.0.33 on Windows 11 with an Intel Core i9-10900K. The fixtures verify their outputs before timing. Use the results to compare the measured workloads.

### Comparison and reporting

The order fixture has 128 lines and two metadata entries. Both comparers check the same fields and short-circuit on the first mismatch.

| Order comparison | Handwritten | Generated | First mismatch report |
| --- | ---: | ---: | ---: |
| Equal | 556 ns | 578 ns | 2.43 µs |
| Change on line 0 | 31 ns | 94 ns | 0.33 µs |
| Change on line 127 | 543 ns | 559 ns | 2.58 µs |

Both Boolean comparers allocated zero bytes in these cases. Matching 128 reordered keyed lines took 16.21 µs and allocated 23.7 KB. The full report in the sync fixture took 8.58 µs for 128 entries and 222 µs for 4,096 entries, with approximately 4.2 KB allocated in each case.

### State-verified sync in 2.2.0

The paired comparison updates eight existing dictionary entries. It alternates builds of 2.1.0 and 2.2.0 in the same default-JIT session and reports the median of 18 measured batches per version. These measurements exclude network and disk I/O.

| Entries | Operation | 2.1.0 | 2.2.0 | 2.1.0 allocation | 2.2.0 allocation |
| ---: | --- | ---: | ---: | ---: | ---: |
| 128 | Create patch | 40.3 µs | 164.1 µs | 26.7 KiB | 48.8 KiB |
| 128 | Receive patch | 25.3 µs | 110.6 µs | 25.2 KiB | 44.9 KiB |
| 128 | Create snapshot | 23.6 µs | 56.7 µs | 21.3 KiB | 24.0 KiB |
| 128 | Receive snapshot | 14.7 µs | 49.1 µs | 16.6 KiB | 19.2 KiB |
| 4,096 | Create patch | 1.48 ms | 6.19 ms | 751 KiB | 1,582 KiB |
| 4,096 | Receive patch | 1.28 ms | 5.74 ms | 750 KiB | 1,495 KiB |
| 4,096 | Create snapshot | 1.44 ms | 3.05 ms | 829 KiB | 917 KiB |
| 4,096 | Receive snapshot | 0.72 ms | 2.33 ms | 658 KiB | 744 KiB |

DES2 adds two digest fields totaling 64 bytes. The verified patch was 230 bytes for both fixtures. Verified snapshots were 2,444 and 87,996 bytes. The added CPU and allocations come from serializing, fingerprinting, and validating the complete state. The raw BDC1 format and its separate API remain available.

### Checked sync versus full snapshot in 2.1.0

Each fixture changes eight existing dictionary entries. The sender creates a checked patch or full snapshot. The receiver applies the patch to an isolated copy or deserializes the new snapshot. Times exclude network and disk I/O.

| Entries | Patch bytes | Snapshot bytes | Create patch | Receive patch | Create snapshot | Receive snapshot |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 128 | 166 | 2,380 | 27.30 µs | 20.63 µs | 18.22 µs | 11.48 µs |
| 4,096 | 166 | 87,932 | 983.09 µs | 689.49 µs | 841.04 µs | 526.44 µs |

The patch cut payload size by 14.3× and 530× in these fixtures. The checked patch spent more CPU on isolation, validation, and comparing the staged result. A fast local link and a low-cost snapshot can favor sending the whole model.

### Existing patch and codec paths

Paired default-JIT runs alternated between builds of 2.0.1 and 2.1.0 on the same 128-entry workload. The patch application operation applies existing scalar and dictionary updates to the same target.

| Operation | 2.0.1 | 2.1.0 | Managed bytes per call |
| --- | ---: | ---: | --- |
| Structured diff | 3.51 µs | 3.60 µs | 1,168 to 1,160 |
| Empty structured diff | 3.34 µs | 3.37 µs | 240 to 232 |
| Compute delta | 3.49 µs | 3.59 µs | 1,712 to 1,704 |
| Apply delta | 153 ns | 152 ns | Zero in both |
| Encode BDC1 | 746 ns | 740 ns | 1,520 in both |
| Decode BDC1 | 350 ns | 346 ns | 920 in both |

Separate measurements showed timing drift. The diff rows use the focused paired repeat.

### Consumer build cost

Seven alternating Release rebuilds of the same OrderComparison sample took a median of 1.44 s with 2.0.1 and 1.28 s with 2.1.0. Restore was completed before timing. These measurements include the compiler and source generator. Windows scheduling produced substantial variation, so the build times are comparable at this scale.

## Reproduce the workloads

```sh
dotnet run -c Release --project DeepEqual.Generator.Benchmarking -- --filter '*DeepEqualityBenchmarks*' '*InventorySyncBenchmarks*' '*FeaturePerformanceBenchmarks*'
```

The comparison fixtures include the handwritten baseline, early and late mismatches, and reordered keyed data. The sync fixtures report byte counts during setup. Release builds can run BenchmarkDotNet with its default job for longer sampling.
