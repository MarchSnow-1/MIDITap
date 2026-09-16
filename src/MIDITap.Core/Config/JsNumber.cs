// JsNumber.cs — 配置解析所依赖的 JavaScript Number(string) 语义
//
// 按 JavaScript 的 Number(...) + Number.isInteger(...) 语义解析 MIDI note 键与 port 字段
// C# 的 double.Parse 行为不同（"" 抛异常、"0x10" 失败、空白规则也不一样）
// 因此这里重现可接受的语法
// 可选符号、十进制/指数形式、0x/0o/0b 进制前缀
// Infinity/NaN、两侧 ECMAScript 空白、"" -> 0
//
// JavaScript Number(string) semantics for config parsing
//
// MIDI note keys and the port field are parsed with JavaScript's Number(...) + Number.isInteger(...) semantics
// C# double.Parse behaves differently ("" throws, "0x10" fails, whitespace rules differ)
// So the accepted grammar is reproduced here
// Optional sign, decimal/exponent form, 0x/0o/0b radix prefixes
// Infinity/NaN, surrounding ECMAScript whitespace, "" -> 0

using System.Globalization;

namespace MIDITap.Core.Config;

public static class JsNumber
{
    public static double ToNumber(string? text)
    {
        if (text is null)
        {
            return double.NaN;
        }

        var trimmed = TrimJsWhitespace(text);
        if (trimmed.Length == 0)
        {
            return 0; // Number("") === 0
        }

        var sign = 1.0;
        var hasSign = false;
        var start = 0;
        if (trimmed[0] == '+')
        {
            hasSign = true;
            start = 1;
        }
        else if (trimmed[0] == '-')
        {
            sign = -1.0;
            hasSign = true;
            start = 1;
        }

        var body = trimmed[start..];
        if (body.Length == 0)
        {
            return double.NaN;
        }

        if (string.Equals(body, "Infinity", StringComparison.Ordinal))
        {
            return sign * double.PositiveInfinity;
        }
        if (string.Equals(body, "NaN", StringComparison.Ordinal))
        {
            // 在 JS 里 Number("-NaN") 是 NaN —— 符号无关紧要
            //
            // Number("-NaN") is NaN in JS; the sign is irrelevant
            return double.NaN;
        }

        // V8 语义：带符号的进制字面量（"-0x10" / "+0b1"）是 NaN
        // 只有十进制形式接受符号
        //
        // V8 semantics: a signed radix literal ("-0x10" / "+0b1") is NaN
        // Only the decimal form accepts a sign
        if (!hasSign && body.Length > 2 && body[0] == '0')
        {
            var c1 = char.ToLowerInvariant(body[1]);
            if (c1 == 'x' && TryParseRadix(body[2..], 16, out var hex))
            {
                return sign * hex;
            }
            if (c1 == 'o' && TryParseRadix(body[2..], 8, out var oct))
            {
                return sign * oct;
            }
            if (c1 == 'b' && TryParseRadix(body[2..], 2, out var bin))
            {
                return sign * bin;
            }
        }

        try
        {
            // 注意不带 AllowLeadingSign：符号已在上面手工消费，否则 "++1" 会被接受（V8 的 Number("++1") 是 NaN）
            //
            // Note the missing AllowLeadingSign: the sign was consumed by hand above
            // Otherwise "++1" would be accepted while V8's Number("++1") is NaN
            return sign * double.Parse(
                body,
                NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture);
        }
        catch (OverflowException)
        {
            // Number("1e400") === Infinity（npm json5 对 1e400 同样得到 Infinity）
            //
            // Number("1e400") === Infinity (npm json5 yields Infinity for 1e400 as well)
            return sign * double.PositiveInfinity;
        }
        catch (FormatException)
        {
            return double.NaN;
        }
    }

    /// <summary>
    /// Number.isInteger：有限且没有小数部分
    ///
    /// Number.isInteger: finite and with no fractional part
    /// </summary>
    public static bool IsInteger(double value)
        => !double.IsNaN(value) && !double.IsInfinity(value) && Math.Floor(value) == value;

    private static bool TryParseRadix(string digits, int radix, out double value)
    {
        value = 0;
        if (digits.Length == 0)
        {
            return false;
        }
        foreach (var ch in digits)
        {
            var d = ch switch
            {
                >= '0' and <= '9' => ch - '0',
                >= 'a' and <= 'f' => ch - 'a' + 10,
                >= 'A' and <= 'F' => ch - 'A' + 10,
                _ => -1,
            };
            if (d < 0 || d >= radix)
            {
                return false;
            }
            value = value * radix + d;
        }
        return true;
    }

    private static string TrimJsWhitespace(string text)
    {
        int start = 0, end = text.Length - 1;
        while (start <= end && IsJsWhitespace(text[start]))
        {
            start++;
        }
        while (end >= start && IsJsWhitespace(text[end]))
        {
            end--;
        }
        return start > end ? string.Empty : text[start..(end + 1)];
    }

    private static bool IsJsWhitespace(char c) => c switch
    {
        ' ' or '\t' or '\n' or '\r' or '\f' or '\v' or '\u00A0' or '\u1680'
            or '\u2000' or '\u2001' or '\u2002' or '\u2003' or '\u2004' or '\u2005'
            or '\u2006' or '\u2007' or '\u2008' or '\u2009' or '\u200A' or '\u2028'
            or '\u2029' or '\u202F' or '\u205F' or '\u3000' or '\uFEFF' => true,
        _ => false,
    };
}
