# Comparison recipes

## Check a migration in a test

Project the old and new results onto the same comparison model. Use the generated assertion to see all differences that matter to the migration.

```csharp
var profile = new ComparisonProfile("Inventory migration", new ComparisonOptions
{
    StringComparison = StringComparison.OrdinalIgnoreCase
});
oldResult.AssertDeepEqual(newResult, profile.Options, new ChangeFormatOptions
{
    RedactPath = path => path.StartsWith("$.Supplier", StringComparison.Ordinal)
});
```

The returned report has structured entries with before and after values. Use `ToText` or `ToJson` with a redaction predicate when logging. A redaction hides both the matching path and its values. Retain raw reports only as long as you need their original model values.

## Skip unnecessary writes

Compare the current and desired snapshots with the normal generated equality API. Compute a patch when the caller needs structured operations. Reuse the same profile to avoid a different tolerance or casing decision in the second stage.

```csharp
if (!existing.AreDeepEqual(desired, profile.Options))
{
    var patch = existing.ComputeDeepDelta(desired, profile.CreateContext());
    var updated = existing.ApplyDeepDelta(patch);
    desired.AssertDeepEqual(updated, profile.Options);
}
```

## Reconcile inventories by key

Use `KeyMembers` on the lines so reordering has no effect on reconciliation. When order carries meaning, set `OrderInsensitive = false` and supply keys to report moves by identifier. Use unique keys for precise results.

Use `ExplainDeepModel()` to see the generated policy. A change report’s `RuntimeFallbacks` entries identify paths where an unregistered runtime type used its normal equality.

## Record a correction or undo

Keep the prior snapshot and use it as the desired state in a new checked update. Assign a new revision to the correction. The receiver can apply the reversal with the same schema and revision checks used for every other update.
