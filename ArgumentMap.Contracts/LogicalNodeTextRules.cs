namespace ArgumentMap.Contracts;

public sealed class InvalidLogicalNodeTextException():InvalidOperationException(LogicalNodeTextRules.Message);

public static class LogicalNodeTextRules
{
    public const string ErrorCode="LogicalNodeLineBreakNotAllowed";
    public const string Message="所有 LN 的值和逻辑语言都只能是单行，不能包含换行；说明可以换行。";
    public static bool HasLineBreak(string? text)=>text is not null&&text.AsSpan().IndexOfAny("\r\n\v\f\u0085\u2028\u2029")>=0;
    public static bool HasLineBreak(LogicalNodeProperties p)=>HasLineBreak(p.Value)||HasLineBreak(p.LogicLanguage);
    public static string SingleLineDraft(string text)=>text.Replace("\r\n"," ").Replace('\r',' ').Replace('\n',' ').Replace('\v',' ').Replace('\f',' ').Replace('\u0085',' ').Replace('\u2028',' ').Replace('\u2029',' ');
    public static void EnsureChangesValid(IEnumerable<LogicalNodeSnapshot> before,IEnumerable<LogicalNodeSnapshot> after)
    {
        var previous=before.ToDictionary(n=>n.Code,StringComparer.Ordinal);
        foreach(var node in after)
            if(!previous.TryGetValue(node.Code,out var old)||node.Properties.Value!=old.Properties.Value||node.Properties.LogicLanguage!=old.Properties.LogicLanguage)
                if(HasLineBreak(node.Properties))throw new InvalidLogicalNodeTextException();
        // Preserve legacy rows during reads, geometry edits, K/J and unrelated writes.
        // Editing a value/expression must explicitly bring that node into compliance.
    }
}
