// Json5TextScanner.cs — "保留注释"的配置写入器（改 name、增删映射）使用的底层文本扫描器
//
// 下标一律按 UTF-16 字符计算，与 JavaScript 的字符串下标一致
//
// Low-level text scanners used by the comment-preserving config writers
// They rename name, add a mapping and delete a mapping
// Index arithmetic operates on UTF-16 chars, matching JavaScript string indices

namespace MIDITap.Core.Config;

public readonly record struct RootRange(int Opening, int Closing);

public readonly record struct TextRange(int Start, int End);

public static class Json5TextScanner
{
    /// <summary>
    /// 定位最外层 JSON5 对象的起始/结束下标；没有根对象（例如内容为数组）时返回 null
    ///
    /// Locate the outermost JSON5 object; null when there is no root object
    /// </summary>
    public static RootRange? FindRootObject(string content)
    {
        char? quote = null;
        var escaped = false;
        var lineComment = false;
        var blockComment = false;
        var depth = 0;
        var opening = -1;

        for (var i = 0; i < content.Length; i++)
        {
            var ch = content[i];
            var next = i + 1 < content.Length ? content[i + 1] : '\0';

            if (lineComment)
            {
                if (ch == '\n' || ch == '\r')
                {
                    lineComment = false;
                }
                continue;
            }
            if (blockComment)
            {
                if (ch == '*' && next == '/')
                {
                    blockComment = false;
                    i++;
                }
                continue;
            }
            if (quote.HasValue)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == quote.Value)
                {
                    quote = null;
                }
                continue;
            }
            if (ch == '/' && next == '/')
            {
                lineComment = true;
                i++;
                continue;
            }
            if (ch == '/' && next == '*')
            {
                blockComment = true;
                i++;
                continue;
            }
            if (ch == '"' || ch == '\'')
            {
                quote = ch;
            }
            else if (ch == '{')
            {
                if (depth == 0)
                {
                    opening = i;
                }
                depth++;
            }
            else if (ch == '}' && depth > 0 && --depth == 0)
            {
                return new RootRange(opening, i);
            }
        }
        return null;
    }

    /// <summary>
    /// 从 start 处开始的字符串的结束引号下标；未终止返回 -1
    ///
    /// Index of the closing quote of the string starting at start; -1 when the string is unterminated
    /// </summary>
    public static int ReadStringEnd(string content, int start)
    {
        var quote = content[start];
        var escaped = false;
        for (var i = start + 1; i < content.Length; i++)
        {
            if (escaped)
            {
                escaped = false;
            }
            else if (content[i] == '\\')
            {
                escaped = true;
            }
            else if (content[i] == quote)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// 跳过空白与注释，以 limit 为边界（line/block comment 的 indexOf 不受 limit 约束）
    ///
    /// Skips whitespace and comments, bounded by limit
    /// The indexOf used for line/block comments is not bounded by limit
    /// </summary>
    public static int SkipTrivia(string content, int start, int limit)
    {
        var i = start;
        while (i < limit)
        {
            if (IsJsWhitespace(content[i]))
            {
                i++;
            }
            else if (content[i] == '/' && i + 1 < content.Length && content[i + 1] == '/')
            {
                var newline = content.IndexOf('\n', i + 2);
                i = newline == -1 ? limit : newline + 1;
            }
            else if (content[i] == '/' && i + 1 < content.Length && content[i + 1] == '*')
            {
                var end = content.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end == -1 ? limit : end + 2;
            }
            else
            {
                break;
            }
        }
        return i;
    }

    private static bool IsIdentifierStart(char c)
        => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' || c == '_' || c == '$' || c > 127;

    private static bool IsIdentifierPart(char c) => IsIdentifierStart(c) || c is >= '0' and <= '9';

    /// <summary>
    /// JSON5 不带引号 IdentifierName 键的结束下标（不含）；start 非标识符开头返回 -1
    ///
    /// End index (exclusive) of an unquoted JSON5 IdentifierName key; -1 when start is not an identifier start
    /// </summary>
    public static int ReadIdentifierEnd(string content, int start, int limit)
    {
        if (start >= limit || !IsIdentifierStart(content[start]))
        {
            return -1;
        }
        var i = start + 1;
        while (i < limit && IsIdentifierPart(content[i]))
        {
            i++;
        }
        return i;
    }

    /// <summary>
    /// 查找顶层字符串属性（带引号或不带引号的键）的字符串 VALUE 的范围 [start, end)
    ///
    /// Finds the range [start, end) of the string VALUE of a top-level string property
    /// That holds whether its key is quoted or not
    /// </summary>
    public static TextRange? FindTopLevelStringValue(string content, string propertyName, RootRange root)
    {
        var objectDepth = 1;
        var arrayDepth = 0;
        for (var i = root.Opening + 1; i < root.Closing; i++)
        {
            var ch = content[i];
            var next = i + 1 < content.Length ? content[i + 1] : '\0';
            if (ch == '/' && next == '/')
            {
                var newline = content.IndexOf('\n', i + 2);
                if (newline == -1)
                {
                    break;
                }
                i = newline;
            }
            else if (ch == '/' && next == '*')
            {
                var end = content.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end == -1)
                {
                    break;
                }
                i = end + 1;
            }
            else if (ch == '"' || ch == '\'')
            {
                var stringEnd = ReadStringEnd(content, i);
                if (stringEnd == -1)
                {
                    return null;
                }
                if (objectDepth == 1 && arrayDepth == 0)
                {
                    var colon = SkipTrivia(content, stringEnd + 1, root.Closing);
                    if (colon < content.Length && content[colon] == ':')
                    {
                        // 按 JSON5 规则解码带引号的键（处理转义写法）
                        //
                        // Decodes a quoted key with JSON5 rules, so escape sequences are handled
                        var key = Json5Parser.ParseQuotedString(content[i..(stringEnd + 1)]);
                        if (key is null)
                        {
                            return null;
                        }
                        var valueStart = SkipTrivia(content, colon + 1, root.Closing);
                        var valueIsString = valueStart < content.Length && (content[valueStart] == '"' || content[valueStart] == '\'');
                        if (key == propertyName && valueIsString)
                        {
                            var valueEnd = ReadStringEnd(content, valueStart);
                            return valueEnd == -1 ? null : new TextRange(valueStart, valueEnd + 1);
                        }
                        if (valueIsString)
                        {
                            var valueEnd = ReadStringEnd(content, valueStart);
                            if (valueEnd == -1)
                            {
                                return null;
                            }
                            i = valueEnd;
                            continue;
                        }
                    }
                }
                i = stringEnd;
            }
            else if (objectDepth == 1 && arrayDepth == 0 && IsIdentifierStart(ch))
            {
                // JSON5 允许不带引号的 IdentifierName 键（如 name: "x"）
                //
                // JSON5 allows an unquoted IdentifierName key (e.g. name: "x")
                var identEnd = ReadIdentifierEnd(content, i, root.Closing);
                if (identEnd != -1)
                {
                    var colon = SkipTrivia(content, identEnd, root.Closing);
                    if (colon < content.Length && content[colon] == ':')
                    {
                        var keyText = content[i..identEnd];
                        var valueStart = SkipTrivia(content, colon + 1, root.Closing);
                        var valueIsString = valueStart < content.Length && (content[valueStart] == '"' || content[valueStart] == '\'');
                        if (keyText == propertyName && valueIsString)
                        {
                            var valueEnd = ReadStringEnd(content, valueStart);
                            return valueEnd == -1 ? null : new TextRange(valueStart, valueEnd + 1);
                        }
                        if (valueIsString)
                        {
                            var valueEnd = ReadStringEnd(content, valueStart);
                            if (valueEnd == -1)
                            {
                                return null;
                            }
                            i = valueEnd;
                        }
                        else
                        {
                            i = identEnd - 1;
                        }
                        continue;
                    }
                    // 不是属性键（如裸的 true/false/null 值）：跳过整个标识符
                    //
                    // Not a property key (e.g. a bare true/false/null value)
                    // The whole identifier is skipped
                    i = identEnd - 1;
                }
            }
            else if (ch == '{')
            {
                objectDepth++;
            }
            else if (ch == '}')
            {
                objectDepth--;
            }
            else if (ch == '[')
            {
                arrayDepth++;
            }
            else if (ch == ']')
            {
                arrayDepth--;
            }
        }
        return null;
    }

    /// <summary>
    /// 查找整个顶层条目（从键一直到逗号）的范围 [start, end)
    /// 该范围用于删除字符串属性
    /// 属性缺失或值不是字符串时返回 null
    ///
    /// Finds the range [start, end) of a whole top-level entry (from its key through the comma)
    /// That range lets a string property be deleted
    /// Null when the property is missing or its value is not a string
    /// </summary>
    public static TextRange? FindTopLevelEntryRange(string content, string propertyName, RootRange root)
    {
        var objectDepth = 1;
        var arrayDepth = 0;
        for (var i = root.Opening + 1; i < root.Closing; i++)
        {
            var ch = content[i];
            var next = i + 1 < content.Length ? content[i + 1] : '\0';
            if (ch == '/' && next == '/')
            {
                var newline = content.IndexOf('\n', i + 2);
                if (newline == -1)
                {
                    break;
                }
                i = newline;
            }
            else if (ch == '/' && next == '*')
            {
                var end = content.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end == -1)
                {
                    break;
                }
                i = end + 1;
            }
            else if (ch == '"' || ch == '\'')
            {
                var stringEnd = ReadStringEnd(content, i);
                if (stringEnd == -1)
                {
                    return null;
                }
                if (objectDepth == 1 && arrayDepth == 0)
                {
                    var colon = SkipTrivia(content, stringEnd + 1, root.Closing);
                    if (colon < content.Length && content[colon] == ':')
                    {
                        var key = Json5Parser.ParseQuotedString(content[i..(stringEnd + 1)]);
                        if (key is null)
                        {
                            return null;
                        }
                        var valueStart = SkipTrivia(content, colon + 1, root.Closing);
                        if (key == propertyName)
                        {
                            // 只删除字符串（键位规格）值；非字符串值不是可移除的映射条目
                            //
                            // Only a string value (a key spec) is deleted
                            // A non-string value is not a removable mapping entry
                            if (valueStart >= root.Closing || (content[valueStart] != '"' && content[valueStart] != '\''))
                            {
                                return null;
                            }
                            var valueEnd = ReadStringEnd(content, valueStart);
                            if (valueEnd == -1)
                            {
                                return null;
                            }
                            var afterValue = SkipTrivia(content, valueEnd + 1, root.Closing);
                            return new TextRange(
                                i,
                                afterValue < content.Length && content[afterValue] == ',' ? afterValue + 1 : valueEnd + 1);
                        }
                        // 不是目标属性：值为字符串时跳过它，避免把字符串内容误读为结构
                        // 非字符串值（数字、布尔、嵌套对象/数组，如 "port": 0）由循环的花括号/方括号跟踪安全遍历，继续向后扫描
                        //
                        // Not the target property: a string value is skipped
                        // That way its contents are never mistaken for structure
                        // A non-string value (a number, boolean or nested object/array such as "port": 0)
                        // It is traversed safely by the loop's brace/bracket tracking
                        // Scanning then continues
                        if (valueStart < content.Length && (content[valueStart] == '"' || content[valueStart] == '\''))
                        {
                            var valueEnd = ReadStringEnd(content, valueStart);
                            if (valueEnd == -1)
                            {
                                return null;
                            }
                            i = valueEnd;
                            continue;
                        }
                    }
                }
                i = stringEnd;
            }
            else if (ch == '{')
            {
                objectDepth++;
            }
            else if (ch == '}')
            {
                objectDepth--;
            }
            else if (ch == '[')
            {
                arrayDepth++;
            }
            else if (ch == ']')
            {
                arrayDepth--;
            }
        }
        return null;
    }

    /// <summary>
    /// 在根对象开头之后插入一个顶层属性（保持既有缩进风格）
    ///
    /// Inserts a top-level property after the root object's opening brace
    /// It keeps the existing indentation style
    /// </summary>
    public static string InsertRootProperty(string content, RootRange root, string propertyName, string serializedValue)
    {
        var lineBreak = content.Contains("\r\n") ? "\r\n" : "\n";
        var rest = content[(root.Opening + 1)..];

        // 仅当 "{" 后紧跟换行时按下一行缩进插入，否则退化为两空格缩进
        //
        // It indents by the next line only when "{" is followed immediately by a newline
        // Otherwise it falls back to two spaces
        int nlLen;
        if (rest.StartsWith("\r\n"))
        {
            nlLen = 2;
        }
        else if (rest.StartsWith('\n') || rest.StartsWith('\r'))
        {
            nlLen = 1;
        }
        else
        {
            nlLen = 0;
        }

        var key = JsonString.Encode(propertyName);
        if (nlLen > 0)
        {
            var insertAt = root.Opening + 1 + nlLen;
            var indent = LeadingIndent(content[insertAt..]);
            return content[..insertAt] + indent + key + ": " + serializedValue + "," + lineBreak + content[insertAt..];
        }
        return content[..(root.Opening + 1)] + lineBreak + "  " + key + ": " + serializedValue + "," + lineBreak
            + content[(root.Opening + 1)..];
    }

    // 取"下一行缩进"：插入点之后的制表符与空格前缀，一个都没有时回退为两个空格
    //
    // The indent of the next line
    // It is the run of tabs and spaces after the insertion point
    // It falls back to two spaces when there is none
    private static string LeadingIndent(string text)
    {
        var i = 0;
        while (i < text.Length && (text[i] == '\t' || text[i] == ' '))
        {
            i++;
        }
        return i == 0 ? "  " : text[..i];
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
