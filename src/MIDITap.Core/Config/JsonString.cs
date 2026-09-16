// JsonString.cs — 单个字符串值的 JavaScript JSON.stringify
//
// 按 JSON.stringify(...) 的规则序列化名称与键位规格
// 引号与反斜杠转义，控制字符写成 \b \t \n \f \r 或 \uXXXX（小写十六进制）
// 其余内容（包括非 ASCII）原样写出
// System.Text.Json 默认会把非 ASCII 转义，所以这里重新实现 JS 的精确行为
//
// JavaScript JSON.stringify for a single string value
//
// Serialises names and key specs the way JSON.stringify(...) does
// Quotes and backslashes are escaped
// Control characters become \b \t \n \f \r or \uXXXX (lowercase hex)
// Everything else — including non-ASCII — is written literally
// System.Text.Json escapes non-ASCII by default
// So the exact JS behaviour is reproduced here instead

using System.Text;

namespace MIDITap.Core.Config;

public static class JsonString
{
    public static string Encode(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u");
                        sb.Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
