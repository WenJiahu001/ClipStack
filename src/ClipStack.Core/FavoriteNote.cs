using System;
using System.Globalization;

namespace ClipStack.Core;

public static class FavoriteNote
{
    public const int MaxLength = 50;

    public static string Normalize(string? value) => (value ?? "").Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();

    // 按用户看到的字符计数，表情和组合字符不会被拆成多个字。
    public static int Count(string value) => new StringInfo(value).LengthInTextElements;

    public static string? Validate(string? value) => Count(Normalize(value)) > MaxLength ? $"备注最多 {MaxLength} 个字，请精简后保存。" : null;

    public static string Prepare(string? value)
    {
        string normalized = Normalize(value);
        if (Validate(normalized) is { } error) throw new ArgumentException(error, nameof(value));
        return normalized;
    }
}
