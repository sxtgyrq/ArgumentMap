using System.Security.Cryptography;

namespace ArgumentMap.Contracts;

public static class LineCode
{
    public const int Length = 10;
    public static bool IsValid(string? value) => value is { Length: Length } && value.All(c => c is >= 'A' and <= 'Z');

    public static string Generate()
    {
        Span<char> code = stackalloc char[Length];
        for (var i = 0; i < code.Length; i++) code[i] = (char)('A' + RandomNumberGenerator.GetInt32(26));
        return new string(code);
    }
}
