; Unshipped analyzer release
; https://github.com/dotnet/roslyn/blob/main/src/RoslynAnalyzers/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
DF001 | DeepEqual.Generator | Warning | Excluded diff member
DF002 | DeepEqual.Generator | Warning | Unordered diff without keys
DF003 | DeepEqual.Generator | Warning | Unavailable element diff
DL001 | DeepEqual.Generator | Warning | Unstable delta member indices
EQ001 | DeepEqual.Generator | Error | Conflicting comparison rules
EQ002 | DeepEqual.Generator | Error | Unavailable deep comparer
EQ003 | DeepEqual.Generator | Error | Invalid collection key members
EQ004 | DeepEqual.Generator | Error | Invalid member comparison rule
EQ005 | DeepEqual.Generator | Error | Unsupported comparison root
EX001 | DeepEqual.Generator | Error | External member cannot be resolved
EX002 | DeepEqual.Generator | Error | Invalid external dictionary side
EX003 | DeepEqual.Generator | Error | Ambiguous external enumerable type
EX004 | DeepEqual.Generator | Error | Unsupported external override
RG001 | DeepEqual.Generator | Error | Missing registry entry

