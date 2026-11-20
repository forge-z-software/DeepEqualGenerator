# Compiler diagnostics

## EQ003

`KeyMembers` must name one to seven readable properties or fields on a collection element. Use `nameof(Line.Sku)` so a rename updates the key. For misspelled string keys with a clear replacement, Visual Studio can suggest an edit.

```csharp
[DeepCompare(KeyMembers = [nameof(Line.Sku)])]
public List<Line> Lines { get; set; } = [];
```

## EQ004

- Apply `Members` and `IgnoreMembers` to the type that declares those members.
- Use `Shallow` for ordinary value equality. The IDE offers this edit when `Reference` was used on a value type.
- A `ComparerType` needs an accessible parameterless constructor and must implement `IEqualityComparer<T>` for the member or its element type.
- Skip pointer and ref-like members with `[DeepCompare(Kind = CompareKind.Skip)]`.

## EQ005

Use a readable class or struct as a comparison root. Open generic definitions in `typeof(...)`, static classes, ref-like types, and inaccessible types cannot be registered. Add `[DeepComparable]` to a generic declaration if you own the type, or register a constructed external type.

## EX004

Register an external root before adding `ExternalDeepCompare`. Use `nameof(ExternalModel.DirectMember)` for a directly readable member.

## Finding generated output

Build the project with `-p:EmitCompilerGeneratedFiles=true` to inspect the generated source under the build intermediate directory. Call `ExplainDeepModel()` to inspect the active rules from application or test code.
