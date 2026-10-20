using System.Globalization;
using System.Text;

namespace ArgumentMap.Contracts;

public sealed record LogicalNodeValueConflict(string Value, string ExistingCode, string ConflictingCode, decimal ExistingX, decimal ExistingY);

public sealed class DuplicateLogicalNodeValueException(LogicalNodeValueConflict conflict) : InvalidOperationException(
    FormattableString.Invariant($"边长为 12 的 LN 值“{conflict.Value}”已被 LN {conflict.ExistingCode}（中心坐标 X={conflict.ExistingX:0.0}, Y={conflict.ExistingY:0.0}）使用，请使用不同的值。"))
{
    public LogicalNodeValueConflict Conflict { get; } = conflict;
}

/// <summary>Only size-12 nodes participate; store and compare their uppercase values.</summary>
public static class LogicalNodeValueUniqueness
{
    // Fixed culture, independent of a user's Windows language (including Turkish I).
    public static string Normalize(string value) => value.ToUpper(CultureInfo.GetCultureInfo("en-US"));

    public static string AvailableValue(string requested, IEnumerable<LogicalNodeSnapshot> nodes)
    {
        requested=Normalize(requested);
        var used = nodes.Where(n => n.SideLength == 12).Select(n => Normalize(n.Properties.Value)).ToHashSet(StringComparer.Ordinal);
        if (!used.Contains(requested)) return requested;
        for (long suffix = 1; ; suffix++)
        {
            var number = suffix.ToString(CultureInfo.InvariantCulture);
            var prefix = new StringBuilder();
            foreach (var rune in requested.EnumerateRunes())
            {
                if (prefix.Length + rune.Utf16SequenceLength + number.Length > 32) break;
                prefix.Append(rune.ToString());
            }
            var candidate = prefix + number;
            if (!used.Contains(candidate)) return candidate;
        }
    }

    public static LogicalNodeValueConflict? FindConflict(IEnumerable<LogicalNodeSnapshot> nodes)
    {
        var values = new Dictionary<string, LogicalNodeSnapshot>(StringComparer.Ordinal);
        foreach (var node in nodes.Where(n => n.SideLength == 12).OrderBy(n => n.Code, StringComparer.Ordinal))
            if (!values.TryAdd(Normalize(node.Properties.Value), node))
            {
                var existing = values[Normalize(node.Properties.Value)];
                return new(Normalize(node.Properties.Value), existing.Code, node.Code, existing.X, existing.Y);
            }
        return null;
    }

    public static LogicalNodeValueConflict? FindConflict(LogicalNodeSnapshot candidate, IEnumerable<LogicalNodeSnapshot> nodes)
    {
        if (candidate.SideLength != 12) return null;
        var existing = nodes.FirstOrDefault(n => n.SideLength == 12 && n.Code != candidate.Code &&
            string.Equals(Normalize(n.Properties.Value), Normalize(candidate.Properties.Value), StringComparison.Ordinal));
        return existing is null ? null : new(Normalize(candidate.Properties.Value), existing.Code, candidate.Code, existing.X, existing.Y);
    }

    public static void EnsureValid(IEnumerable<LogicalNodeSnapshot> nodes)
    {
        if (FindConflict(nodes) is { } conflict) throw new DuplicateLogicalNodeValueException(conflict);
        if(nodes.Any(n=>n.SideLength==12 && n.Properties.Value!=Normalize(n.Properties.Value)))
            throw new InvalidOperationException("边长为 12 的 LN 值必须统一为大写后保存。");
    }
}
