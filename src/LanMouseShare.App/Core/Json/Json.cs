using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LanMouseShare.Core.Json
{
    /// <summary>
    /// 极简 JSON 读写器（配置管理代理产出）。
    ///
    /// 为什么不直接用 DataContractJsonSerializer 或 JavaScriptSerializer？
    ///   1. DataContractJsonSerializer 输出是单行、字段顺序按字母排，人没法手工改；
    ///   2. JavaScriptSerializer 要额外引用 System.Web.Extensions，
    ///      在精简版 .NET Framework 4.8（Win7 手动安装的那种）上不一定在；
    ///   3. 配置文件是给人看的，必须能漂亮缩进，也必须能在被改坏时优雅退回默认值。
    ///
    /// 所以这里手写一个约 300 行的解析器：零依赖、Win7 可用、出错信息带位置。
    /// 支持的值类型：对象 -> Dictionary&lt;string, object&gt;，数组 -> List&lt;object&gt;，
    /// 字符串 -> string，数字 -> double，布尔 -> bool，空 -> null。
    /// </summary>
    public static class Json
    {
        // ------------------------------------------------------------ 解析

        public static object Parse(string text)
        {
            if (text == null)
            {
                throw new ArgumentNullException("text");
            }

            int index = 0;
            object value = ParseValue(text, ref index);
            SkipWhitespace(text, ref index);
            if (index < text.Length)
            {
                throw new FormatException("JSON 解析失败：位置 " + index.ToString(CultureInfo.InvariantCulture) + " 之后还有多余内容");
            }

            return value;
        }

        /// <summary>解析失败时不抛异常，返回 null 并把原因写进 error。</summary>
        public static object TryParse(string text, out string error)
        {
            error = null;
            try
            {
                return Parse(text);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        private static object ParseValue(string text, ref int index)
        {
            SkipWhitespace(text, ref index);
            if (index >= text.Length)
            {
                throw new FormatException("JSON 解析失败：内容提前结束");
            }

            char c = text[index];
            switch (c)
            {
                case '{':
                    return ParseObject(text, ref index);
                case '[':
                    return ParseArray(text, ref index);
                case '"':
                    return ParseString(text, ref index);
                case 't':
                    Expect(text, ref index, "true");
                    return true;
                case 'f':
                    Expect(text, ref index, "false");
                    return false;
                case 'n':
                    Expect(text, ref index, "null");
                    return null;
                default:
                    return ParseNumber(text, ref index);
            }
        }

        private static Dictionary<string, object> ParseObject(string text, ref int index)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            index++; // {
            SkipWhitespace(text, ref index);

            if (index < text.Length && text[index] == '}')
            {
                index++;
                return result;
            }

            while (true)
            {
                SkipWhitespace(text, ref index);
                if (index >= text.Length || text[index] != '"')
                {
                    throw new FormatException("JSON 解析失败：位置 " + index.ToString(CultureInfo.InvariantCulture) + " 期望一个字段名");
                }

                string key = ParseString(text, ref index);
                SkipWhitespace(text, ref index);

                if (index >= text.Length || text[index] != ':')
                {
                    throw new FormatException("JSON 解析失败：字段 \"" + key + "\" 之后缺少冒号");
                }

                index++;
                result[key] = ParseValue(text, ref index);
                SkipWhitespace(text, ref index);

                if (index >= text.Length)
                {
                    throw new FormatException("JSON 解析失败：对象没有闭合");
                }

                if (text[index] == ',')
                {
                    index++;
                    continue;
                }

                if (text[index] == '}')
                {
                    index++;
                    return result;
                }

                throw new FormatException("JSON 解析失败：位置 " + index.ToString(CultureInfo.InvariantCulture) + " 期望 ',' 或 '}'");
            }
        }

        private static List<object> ParseArray(string text, ref int index)
        {
            List<object> result = new List<object>();
            index++; // [
            SkipWhitespace(text, ref index);

            if (index < text.Length && text[index] == ']')
            {
                index++;
                return result;
            }

            while (true)
            {
                result.Add(ParseValue(text, ref index));
                SkipWhitespace(text, ref index);

                if (index >= text.Length)
                {
                    throw new FormatException("JSON 解析失败：数组没有闭合");
                }

                if (text[index] == ',')
                {
                    index++;
                    continue;
                }

                if (text[index] == ']')
                {
                    index++;
                    return result;
                }

                throw new FormatException("JSON 解析失败：位置 " + index.ToString(CultureInfo.InvariantCulture) + " 期望 ',' 或 ']'");
            }
        }

        private static string ParseString(string text, ref int index)
        {
            index++; // 开引号
            StringBuilder sb = new StringBuilder();

            while (index < text.Length)
            {
                char c = text[index++];

                if (c == '"')
                {
                    return sb.ToString();
                }

                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (index >= text.Length)
                {
                    break;
                }

                char esc = text[index++];
                switch (esc)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (index + 4 > text.Length)
                        {
                            throw new FormatException("JSON 解析失败：\\u 转义不完整");
                        }
                        string hex = text.Substring(index, 4);
                        index += 4;
                        ushort code;
                        if (!ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                        {
                            throw new FormatException("JSON 解析失败：非法的 \\u 转义 " + hex);
                        }
                        sb.Append((char)code);
                        break;
                    default:
                        throw new FormatException("JSON 解析失败：未知转义 \\" + esc);
                }
            }

            throw new FormatException("JSON 解析失败：字符串没有闭合");
        }

        private static object ParseNumber(string text, ref int index)
        {
            int start = index;
            while (index < text.Length && "-+.eE0123456789".IndexOf(text[index]) >= 0)
            {
                index++;
            }

            string raw = text.Substring(start, index - start);
            double value;
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                throw new FormatException("JSON 解析失败：位置 " + start.ToString(CultureInfo.InvariantCulture) + " 不是合法数字（" + raw + "）");
            }

            return value;
        }

        private static void Expect(string text, ref int index, string literal)
        {
            if (index + literal.Length > text.Length ||
                string.CompareOrdinal(text, index, literal, 0, literal.Length) != 0)
            {
                throw new FormatException("JSON 解析失败：位置 " + index.ToString(CultureInfo.InvariantCulture) + " 期望 " + literal);
            }

            index += literal.Length;
        }

        private static void SkipWhitespace(string text, ref int index)
        {
            while (index < text.Length)
            {
                char c = text[index];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                {
                    index++;
                    continue;
                }
                break;
            }
        }

        // ------------------------------------------------------------ 输出

        public static string Stringify(object value)
        {
            return Stringify(value, true);
        }

        public static string Stringify(object value, bool pretty)
        {
            StringBuilder sb = new StringBuilder();
            Write(sb, value, pretty, 0);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object value, bool pretty, int depth)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }

            string text = value as string;
            if (text != null)
            {
                WriteString(sb, text);
                return;
            }

            if (value is bool)
            {
                sb.Append(((bool)value) ? "true" : "false");
                return;
            }

            if (value is int || value is long || value is short || value is byte)
            {
                sb.Append(Convert.ToInt64(value).ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (value is double || value is float || value is decimal)
            {
                double d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (Math.Abs(d - Math.Round(d)) < 1e-9 && Math.Abs(d) < 9e15)
                {
                    sb.Append(((long)Math.Round(d)).ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                }
                return;
            }

            Dictionary<string, object> map = value as Dictionary<string, object>;
            if (map != null)
            {
                WriteObject(sb, map, pretty, depth);
                return;
            }

            System.Collections.IEnumerable list = value as System.Collections.IEnumerable;
            if (list != null)
            {
                WriteArray(sb, list, pretty, depth);
                return;
            }

            WriteString(sb, value.ToString());
        }

        private static void WriteObject(StringBuilder sb, Dictionary<string, object> map, bool pretty, int depth)
        {
            if (map.Count == 0)
            {
                sb.Append("{}");
                return;
            }

            sb.Append('{');
            bool first = true;

            foreach (KeyValuePair<string, object> pair in map)
            {
                if (!first)
                {
                    sb.Append(',');
                }
                first = false;

                if (pretty)
                {
                    sb.Append('\n');
                    Indent(sb, depth + 1);
                }

                WriteString(sb, pair.Key);
                sb.Append(pretty ? ": " : ":");
                Write(sb, pair.Value, pretty, depth + 1);
            }

            if (pretty)
            {
                sb.Append('\n');
                Indent(sb, depth);
            }

            sb.Append('}');
        }

        private static void WriteArray(StringBuilder sb, System.Collections.IEnumerable list, bool pretty, int depth)
        {
            List<object> items = new List<object>();
            foreach (object item in list)
            {
                items.Add(item);
            }

            if (items.Count == 0)
            {
                sb.Append("[]");
                return;
            }

            sb.Append('[');
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                if (pretty)
                {
                    sb.Append('\n');
                    Indent(sb, depth + 1);
                }

                Write(sb, items[i], pretty, depth + 1);
            }

            if (pretty)
            {
                sb.Append('\n');
                Indent(sb, depth);
            }

            sb.Append(']');
        }

        private static void Indent(StringBuilder sb, int depth)
        {
            for (int i = 0; i < depth; i++)
            {
                sb.Append("  ");
            }
        }

        private static void WriteString(StringBuilder sb, string text)
        {
            sb.Append('"');
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }

        // ------------------------------------------------------------ 取值助手

        public static Dictionary<string, object> AsObject(object value)
        {
            return value as Dictionary<string, object>;
        }

        public static string GetString(Dictionary<string, object> map, string key, string fallback)
        {
            object value;
            if (map != null && map.TryGetValue(key, out value))
            {
                string text = value as string;
                if (text != null)
                {
                    return text;
                }
                if (value != null && !(value is Dictionary<string, object>))
                {
                    return Convert.ToString(value, CultureInfo.InvariantCulture);
                }
            }

            return fallback;
        }

        public static int GetInt(Dictionary<string, object> map, string key, int fallback, int min, int max)
        {
            int result = fallback;
            object value;
            if (map != null && map.TryGetValue(key, out value) && value != null)
            {
                try
                {
                    result = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                }
                catch (Exception)
                {
                    result = fallback;
                }
            }

            if (result < min) result = min;
            if (result > max) result = max;
            return result;
        }

        public static bool GetBool(Dictionary<string, object> map, string key, bool fallback)
        {
            object value;
            if (map != null && map.TryGetValue(key, out value) && value != null)
            {
                if (value is bool)
                {
                    return (bool)value;
                }

                string text = value as string;
                if (text != null)
                {
                    if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)) return true;
                    if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase)) return false;
                }

                try
                {
                    return Convert.ToDouble(value, CultureInfo.InvariantCulture) != 0;
                }
                catch (Exception)
                {
                    return fallback;
                }
            }

            return fallback;
        }
    }
}
