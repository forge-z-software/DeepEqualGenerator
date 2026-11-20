using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DeepEqual;

public sealed class ChangeFormatOptions
{
    public int MaxValueLength { get; init; } = 120;
    public Func<string, bool>? RedactPath { get; init; }
    public bool IncludeValues { get; init; } = true;
}

public sealed record DeepComparisonFallback(string Path, string TypeName);

public sealed class DeepChangeReport
{
    internal DeepChangeReport(DeepMismatch[] changes, DeepComparisonFallback[] fallbacks, bool truncated)
    {
        Changes = Array.AsReadOnly(changes);
        RuntimeFallbacks = Array.AsReadOnly(fallbacks);
        IsTruncated = truncated;
    }

    public IReadOnlyList<DeepMismatch> Changes { get; }
    public IReadOnlyList<DeepComparisonFallback> RuntimeFallbacks { get; }
    public bool HasChanges => Changes.Count != 0;
    public bool IsTruncated { get; }

    public string ToText(ChangeFormatOptions? options = null)
    {
        var formatter = new ReportFormatter(options, HasChanges);
        var text = new StringBuilder();
        foreach (var change in Changes)
            ReportPresentation.AppendChange(text, formatter.Format(change));
        return ReportPresentation.FinishText(text, IsTruncated);
    }

    /// <summary>Renders an offline HTML report with side-by-side values and filters.</summary>
    public string ToHtml(ChangeFormatOptions? options = null) => DeepChangeReportHtml.Render(Capture(options));

    public string ToJson(ChangeFormatOptions? options = null)
    {
        var formatter = new ReportFormatter(options, HasChanges);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteBoolean("truncated", IsTruncated);
        writer.WriteStartArray("changes");
        foreach (var mismatch in Changes)
        {
            var change = formatter.Format(mismatch);
            writer.WriteStartObject();
            writer.WriteString("path", change.Path);
            writer.WriteString("kind", change.Kind.ToString());
            if (change.Before is not null) writer.WriteString("before", change.Before);
            if (change.After is not null) writer.WriteString("after", change.After);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("runtimeFallbacks");
        foreach (var runtimeFallback in RuntimeFallbacks)
        {
            var fallback = formatter.Format(runtimeFallback);
            writer.WriteStartObject();
            writer.WriteString("path", fallback.Path);
            if (fallback.TypeName is not null) writer.WriteString("type", fallback.TypeName);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal ReportPresentation Capture(ChangeFormatOptions? options)
    {
        var formatter = new ReportFormatter(options, HasChanges);
        var changes = new DisplayChange[Changes.Count];
        var fallbacks = new DisplayFallback[RuntimeFallbacks.Count];
        for (var i = 0; i < changes.Length; i++)
            changes[i] = formatter.Format(Changes[i]);
        for (var i = 0; i < fallbacks.Length; i++)
            fallbacks[i] = formatter.Format(RuntimeFallbacks[i]);
        return new(changes, fallbacks, IsTruncated);
    }

    private readonly struct ReportFormatter
    {
        private static readonly ChangeFormatOptions DefaultOptions = new();
        private readonly ChangeFormatOptions _options;
        private readonly Dictionary<string, bool>? _decisions;

        internal ReportFormatter(ChangeFormatOptions? options, bool hasChanges)
        {
            _options = options ?? DefaultOptions;
            if (hasChanges) ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxValueLength);
            _decisions = _options.RedactPath is null ? null : new(StringComparer.Ordinal);
        }

        internal DisplayChange Format(DeepMismatch change)
        {
            if (Redact(change.Path))
                return new("[redacted]", change.Kind, null, null, IsRedacted: true);

            var before = _options.IncludeValues && change.Kind is not (DeepMismatchKind.Added or DeepMismatchKind.Set)
                ? FormatValue(change.Expected, _options.MaxValueLength) : null;
            var after = _options.IncludeValues && change.Kind is not (DeepMismatchKind.Removed or DeepMismatchKind.MissingKey)
                ? FormatValue(change.Actual, _options.MaxValueLength) : null;
            return new(change.Path, change.Kind, before, after);
        }

        internal DisplayFallback Format(DeepComparisonFallback fallback) =>
            Redact(fallback.Path) ? new("[redacted]", null) : new(fallback.Path, fallback.TypeName);

        private bool Redact(string path)
        {
            if (_decisions is null) return false;
            if (!_decisions.TryGetValue(path, out var hide))
                _decisions.Add(path, hide = _options.RedactPath!(path));
            return hide;
        }
    }

    internal static string FormatValue(object? value, int limit)
    {
        if (value is string str)
        {
            if (str.Length <= limit) return Quote(str);
            if (char.IsSurrogatePair(str[limit - 1], str[limit])) limit--;
            return Quote(str[..limit] + "…");
        }
        return value switch
        {
            null => "null",
            char character => Quote(character.ToString()),
            bool boolean => boolean ? "true" : "false",
            DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
            byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal
                or Guid or TimeSpan or DateOnly or TimeOnly or Enum =>
                ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture),
            _ => $"<{value.GetType().Name}>"
        };
    }

    private static string Quote(string value) => "\"" + JsonEncodedText.Encode(value) + "\"";
}

internal readonly record struct DisplayChange(string Path, DeepMismatchKind Kind, string? Before, string? After,
    bool IsRedacted = false);

internal readonly record struct DisplayFallback(string Path, string? TypeName);

internal sealed record ReportPresentation(DisplayChange[] Changes, DisplayFallback[] Fallbacks, bool IsTruncated)
{
    internal string ToText()
    {
        var text = new StringBuilder();
        foreach (var change in Changes)
            AppendChange(text, change);
        return FinishText(text, IsTruncated);
    }

    internal static string FinishText(StringBuilder text, bool truncated)
    {
        if (truncated) text.AppendLine().Append("Additional changes omitted.");
        return text.ToString();
    }

    internal static void AppendChange(StringBuilder text, DisplayChange change)
    {
        var (path, kind, before, after, _) = change;
        if (text.Length > 0) text.AppendLine();
        text.Append(path).Append(' ');
        switch (kind)
        {
            case DeepMismatchKind.Added:
                text.Append("added");
                if (after is not null) text.Append(' ').Append(after);
                break;
            case DeepMismatchKind.Set:
                text.Append("set");
                if (after is not null) text.Append(' ').Append(after);
                break;
            case DeepMismatchKind.Moved:
                text.Append("moved");
                if (before is not null) text.Append(" from index ").Append(before).Append(" to ").Append(after);
                break;
            case DeepMismatchKind.Removed:
            case DeepMismatchKind.MissingKey:
                text.Append("removed");
                if (before is not null) text.Append(' ').Append(before);
                break;
            default:
                if (before is null)
                    text.Append("changed");
                else
                    text.Append(before).Append(" → ").Append(after);
                break;
        }
    }
}

public sealed class DeepComparisonException : Exception
{
    private readonly ReportPresentation _presentation;

    public DeepComparisonException(DeepChangeReport report, ChangeFormatOptions? format = null)
        : this(report, report.Capture(format))
    {
    }

    private DeepComparisonException(DeepChangeReport report, ReportPresentation presentation)
        : base("Deep comparison failed." + Environment.NewLine + presentation.ToText())
    {
        Report = report;
        _presentation = presentation;
    }

    public DeepChangeReport Report { get; }

    /// <summary>Renders the values and redaction choices captured when the assertion failed.</summary>
    public string ToHtml() => DeepChangeReportHtml.Render(_presentation);
}
