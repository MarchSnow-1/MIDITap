// Json5Parser.cs — 配置读取路径用的最小 JSON5 解析器
//
// 本解析器接受的语法子集
// // 与块注释、尾随逗号、单/双引号字符串与键名
// 不带引号的 IdentifierName 键、十六进制/八进制/二进制数字
// Infinity/NaN 字面量、ES5 字符串转义
// 其余一律抛异常，由 loadConfig 返回 null
//
// Minimal JSON5 parser for the config read path
//
// The subset this parser accepts
// // and block comments, trailing commas
// Single/double-quoted strings and keys, unquoted IdentifierName keys
// Hex/octal/binary numbers, Infinity/NaN literals, ES5 string escapes
// Anything else throws, and loadConfig returns null

using System.Globalization;

namespace MIDITap.Core.Config;

public sealed class Json5ParseException(string message, int position)
    : FormatException($"{message} (at offset {position})");

public static class Json5Parser
{
    public static Json5Value Parse(string text)
    {
        var parser = new Parser(text);
        var value = parser.ParseValue();
        parser.SkipTrivia();
        if (!parser.IsEnd)
        {
            throw parser.Error("Unexpected trailing content");
        }
        return value;
    }

    /// <summary>
    /// 仅解析一段带引号的字符串文本（键名解码用）
    ///
    /// Parses one quoted string literal only (key-name decoding)
    /// </summary>
    public static string? ParseQuotedString(string text)
    {
        try
        {
            if (text.Length == 0 || (text[0] != '"' && text[0] != '\''))
            {
                return null;
            }
            var parser = new Parser(text);
            return parser.ParseString();
        }
        catch (Json5ParseException)
        {
            return null;
        }
    }

    private sealed class Parser(string text)
    {
        private int _i;

        public bool IsEnd => _i >= text.Length;

        private char Cur => _i < text.Length ? text[_i] : '\0';

        private char Peek(int ahead = 1) => _i + ahead < text.Length ? text[_i + ahead] : '\0';

        public Json5ParseException Error(string message) => new(message, _i);

        public void SkipTrivia()
        {
            while (!IsEnd)
            {
                var c = Cur;
                if (IsJson5Whitespace(c))
                {
                    _i++;
                }
                else if (c == '/' && Peek() == '/')
                {
                    while (!IsEnd && text[_i] != '\n')
                    {
                        _i++;
                    }
                }
                else if (c == '/' && Peek() == '*')
                {
                    var end = text.IndexOf("*/", _i + 2, StringComparison.Ordinal);
                    if (end == -1)
                    {
                        throw Error("Unterminated block comment");
                    }
                    _i = end + 2;
                }
                else
                {
                    break;
                }
            }
        }

        public Json5Value ParseValue()
        {
            SkipTrivia();
            if (IsEnd)
            {
                throw Error("Unexpected end of input");
            }
            return Cur switch
            {
                '{' => ParseObject(),
                '[' => ParseArray(),
                '"' or '\'' => new Json5Value(Json5Kind.String) { StringValue = ParseString() },
                _ => ParseNumberOrLiteral(),
            };
        }

        private Json5Value ParseObject()
        {
            var result = new Json5Value(Json5Kind.Object);
            _i++; // '{'
            while (true)
            {
                SkipTrivia();
                if (IsEnd)
                {
                    throw Error("Unterminated object");
                }
                if (Cur == '}')
                {
                    _i++;
                    return result;
                }

                string key;
                if (Cur == '"' || Cur == '\'')
                {
                    key = ParseString();
                }
                else if (IsIdentifierStart(Cur))
                {
                    var start = _i;
                    while (!IsEnd && IsIdentifierPart(text[_i]))
                    {
                        _i++;
                    }
                    key = text[start.._i];
                }
                else
                {
                    throw Error("Invalid object key");
                }

                SkipTrivia();
                if (Cur != ':')
                {
                    throw Error("Expected ':' after object key");
                }
                _i++;

                var value = ParseValue();
                // JS 语义：重复键保留首次出现位置，后面的值覆盖前面的值
                //
                // JS semantics: duplicate keys keep the first position
                // The later value overwrites the earlier one
                var existing = result.ObjectEntries.FindIndex(e => e.Key == key);
                if (existing >= 0)
                {
                    result.ObjectEntries[existing] = new KeyValuePair<string, Json5Value?>(key, value);
                }
                else
                {
                    result.ObjectEntries.Add(new(key, value));
                }

                SkipTrivia();
                if (Cur == ',')
                {
                    _i++;
                }
                else if (Cur != '}')
                {
                    throw Error("Expected ',' or '}' in object");
                }
            }
        }

        private Json5Value ParseArray()
        {
            var result = new Json5Value(Json5Kind.Array);
            _i++; // '['
            while (true)
            {
                SkipTrivia();
                if (IsEnd)
                {
                    throw Error("Unterminated array");
                }
                if (Cur == ']')
                {
                    _i++;
                    return result;
                }

                result.ArrayValue.Add(ParseValue());

                SkipTrivia();
                if (Cur == ',')
                {
                    _i++;
                }
                else if (Cur != ']')
                {
                    throw Error("Expected ',' or ']' in array");
                }
            }
        }

        public string ParseString()
        {
            var quote = Cur;
            if (quote != '"' && quote != '\'')
            {
                throw Error("Expected quoted string");
            }
            _i++;
            var sb = new System.Text.StringBuilder();
            while (true)
            {
                if (IsEnd)
                {
                    throw Error("Unterminated string");
                }
                var c = Cur;
                if (c == quote)
                {
                    _i++;
                    return sb.ToString();
                }
                if (c == '\\')
                {
                    _i++;
                    if (IsEnd)
                    {
                        throw Error("Unterminated string escape");
                    }
                    var esc = Cur;
                    _i++;
                    switch (esc)
                    {
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'v': sb.Append('\v'); break;
                        case '0':
                            // json5 2.x：\0 后跟数字是非法转义
                            //
                            // json5 2.x: a \0 followed by a digit is an invalid escape
                            if (!IsEnd && char.IsAsciiDigit(Cur))
                            {
                                throw Error("Invalid escape \\0 followed by a digit");
                            }
                            sb.Append('\0');
                            break;
                        case 'x':
                            sb.Append((char)ParseHex(2));
                            break;
                        case 'u':
                            sb.Append((char)ParseHex(4));
                            break;
                        case '\r':
                            // 行继续符：\r\n 或 \r 产生空串
                            //
                            // Line continuation: \r\n or \r yields an empty string
                            if (Cur == '\n')
                            {
                                _i++;
                            }
                            break;
                        case '\n':
                            break;
                        default:
                            sb.Append(esc);
                            break;
                    }
                }
                else if (c == '\n' || c == '\r')
                {
                    // JSON5 语法：字符串字面量内不允许裸换行（npm json5 同样抛错）
                    //
                    // JSON5 syntax: a bare newline inside a string literal is not allowed
                    // npm json5 throws too
                    throw Error("Unterminated string (raw line terminator in string)");
                }
                else
                {
                    sb.Append(c);
                    _i++;
                }
            }
        }

        private int ParseHex(int digits)
        {
            var value = 0;
            for (var n = 0; n < digits; n++)
            {
                if (IsEnd)
                {
                    throw Error("Invalid unicode escape");
                }
                var d = HexDigit(Cur);
                if (d < 0)
                {
                    throw Error("Invalid unicode escape");
                }
                value = value * 16 + d;
                _i++;
            }
            return value;
        }

        private Json5Value ParseNumberOrLiteral()
        {
            var start = _i;

            // 字面量优先：true / false / null（JSON5 允许不带引号）
            //
            // Literals first: true / false / null, which JSON5 allows unquoted
            if (MatchWord("true"))
            {
                return new Json5Value(Json5Kind.Bool) { BoolValue = true };
            }
            if (MatchWord("false"))
            {
                return new Json5Value(Json5Kind.Bool) { BoolValue = false };
            }
            if (MatchWord("null"))
            {
                return Json5Value.Null;
            }

            var sign = 1.0;
            if (Cur == '+' || Cur == '-')
            {
                if (Cur == '-')
                {
                    sign = -1.0;
                }
                _i++;
            }

            // Infinity / NaN 裸字面量（JSON5 允许，可带符号）
            //
            // Bare Infinity / NaN literals, which JSON5 allows with an optional sign
            if (MatchWord("Infinity"))
            {
                return new Json5Value(Json5Kind.Number) { NumberValue = sign * double.PositiveInfinity };
            }
            if (MatchWord("NaN"))
            {
                return new Json5Value(Json5Kind.Number) { NumberValue = double.NaN };
            }

            // JSON5 数字只支持 0x 十六进制前缀
            // 0o/0b 是 JS Number() 的扩展，npm json5 会直接抛错，这里保持一致地拒绝
            // 注意：json5 的词法允许带符号的十六进制（"-0x10" -> -16）
            // 符号在此处应用，而不是交给 V8 语义的 JsNumber（后者对带符号进制返回 NaN）
            //
            // JSON5 numbers only support the 0x hex prefix
            // 0o/0b are JS Number() extensions that npm json5 rejects outright
            // This parser rejects them too
            // Note: json5's lexer allows a signed hex literal ("-0x10" -> -16)
            // The sign is applied here
            // It is not left to the V8-semantics JsNumber, which returns NaN for a signed radix prefix
            if (Cur == '0' && Peek() is 'x' or 'X')
            {
                var radixStart = _i;
                _i += 2;
                while (!IsEnd && IsHexDigit(Cur))
                {
                    _i++;
                }
                var radixToken = text[radixStart.._i];
                var radixValue = JsNumber.ToNumber(radixToken);
                if (double.IsNaN(radixValue))
                {
                    throw Error($"Invalid number '{radixToken}'");
                }
                return new Json5Value(Json5Kind.Number) { NumberValue = sign * radixValue };
            }

            // 十进制：digits [. digits] [exp] 或 . digits [exp]
            //
            // Decimal: digits [. digits] [exp], or . digits [exp]
            var numStart = _i;
            var sawDigit = false;
            while (!IsEnd && char.IsAsciiDigit(Cur))
            {
                _i++;
                sawDigit = true;
            }
            // JSON5 禁止前导零（"048" 非法，"0" 与 "0.5" 合法）
            //
            // JSON5 forbids leading zeros ("048" is invalid; "0" and "0.5" are valid)
            if (!IsEnd && text[numStart] == '0' && _i > numStart + 1)
            {
                throw Error($"Invalid number '{text[start.._i]}': leading zeros are not allowed");
            }
            if (!IsEnd && Cur == '.')
            {
                _i++;
                while (!IsEnd && char.IsAsciiDigit(Cur))
                {
                    _i++;
                    sawDigit = true;
                }
            }
            if (sawDigit && !IsEnd && (Cur == 'e' || Cur == 'E'))
            {
                var expMark = _i;
                _i++;
                if (!IsEnd && (Cur == '+' || Cur == '-'))
                {
                    _i++;
                }
                var expDigits = 0;
                while (!IsEnd && char.IsAsciiDigit(Cur))
                {
                    _i++;
                    expDigits++;
                }
                if (expDigits == 0)
                {
                    _i = expMark; // 不是合法指数，回退；后续会解析失败。
                }
            }
            if (!sawDigit)
            {
                throw Error($"Unexpected character '{Cur}'");
            }

            var token = text[start.._i];
            var value = JsNumber.ToNumber(token);
            if (double.IsNaN(value))
            {
                throw Error($"Invalid number '{token}'");
            }
            return new Json5Value(Json5Kind.Number) { NumberValue = value };
        }

        private bool MatchWord(string word)
        {
            if (text.Length - _i >= word.Length && string.CompareOrdinal(text, _i, word, 0, word.Length) == 0)
            {
                // 关键字后不能紧跟标识符字符（如 "trueish" 不是 true）
                //
                // A keyword must not be followed by an identifier character ("trueish" is not true)
                var after = _i + word.Length;
                if (after >= text.Length || !IsIdentifierPart(text[after]))
                {
                    _i = after;
                    return true;
                }
            }
            return false;
        }

        private static bool IsJson5Whitespace(char c) => c switch
        {
            ' ' or '\t' or '\n' or '\r' or '\f' or '\v' or '\u00A0' or '\u1680'
                or '\u2000' or '\u2001' or '\u2002' or '\u2003' or '\u2004' or '\u2005'
                or '\u2006' or '\u2007' or '\u2008' or '\u2009' or '\u200A' or '\u2028'
                or '\u2029' or '\u202F' or '\u205F' or '\u3000' or '\uFEFF' => true,
            _ => false,
        };

        private static bool IsIdentifierStart(char c)
            => char.IsAsciiLetter(c) || c == '_' || c == '$' || c > 127;

        private static bool IsIdentifierPart(char c)
            => IsIdentifierStart(c) || char.IsAsciiDigit(c) || c == '\u200C' || c == '\u200D';

        private static int HexDigit(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };

        private static bool IsHexDigit(char c) => HexDigit(c) >= 0;

    }
}
