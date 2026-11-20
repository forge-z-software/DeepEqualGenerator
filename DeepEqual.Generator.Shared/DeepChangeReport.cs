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
        options ??= new();
        var text = new StringBuilder();
        foreach (var change in Changes)
        {
            var (path, before, after) = Format(change, options);
            if (text.Length > 0) text.AppendLine();
            text.Append(path).Append(' ');
            switch (change.Kind)
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
        if (IsTruncated) text.AppendLine().Append("Additional changes omitted.");
        return text.ToString();
    }

    public string ToJson(ChangeFormatOptions? options = null)
    {
        options ??= new();
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteBoolean("truncated", IsTruncated);
        writer.WriteStartArray("changes");
        foreach (var change in Changes)
        {
            var (path, before, after) = Format(change, options);
            writer.WriteStartObject();
            writer.WriteString("path", path);
            writer.WriteString("kind", change.Kind.ToString());
            if (before is not null && change.Kind is not (DeepMismatchKind.Added or DeepMismatchKind.Set)) writer.WriteString("before", before);
            if (after is not null && change.Kind is not (DeepMismatchKind.Removed or DeepMismatchKind.MissingKey))
                writer.WriteString("after", after);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("runtimeFallbacks");
        foreach (var fallback in RuntimeFallbacks)
        {
            writer.WriteStartObject();
            var redact = options.RedactPath?.Invoke(fallback.Path) == true;
            writer.WriteString("path", redact ? "[redacted]" : fallback.Path);
            if (!redact) writer.WriteString("type", fallback.TypeName);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static (string Path, string? Before, string? After) Format(DeepMismatch change, ChangeFormatOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxValueLength);
        if (options.RedactPath?.Invoke(change.Path) == true) return ("[redacted]", null, null);
        return options.IncludeValues
            ? (change.Path, FormatValue(change.Expected, options.MaxValueLength), FormatValue(change.Actual, options.MaxValueLength))
            : (change.Path, null, null);
    }

    internal static string FormatValue(object? value, int limit)
    {
        if (value is string str)
            return Quote(str.Length <= limit ? str : str[..limit] + "…");
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

public sealed class DeepComparisonException : Exception
{
    public DeepComparisonException(DeepChangeReport report, ChangeFormatOptions? format = null)
        : base("Deep comparison failed." + Environment.NewLine + report.ToText(format)) => Report = report;

    public DeepChangeReport Report { get; }
}
