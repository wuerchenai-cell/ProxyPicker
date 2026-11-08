using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ProxyPicker.Core
{
    /// <summary>
    /// 极简 JSON 解析器：只做「读」，把 JSON 变成 Dictionary / List / string / double / bool / null。
    /// 为什么不用 System.Web.Extensions：本程序只需要读少数字段，
    /// 自己解析可避免额外程序集依赖，也让单文件体积和启动开销更小。
    /// </summary>
    internal static class Json
    {
        public static object Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int i = 0;
            object value = ParseValue(text, ref i, 0);
            SkipWs(text, ref i);
            if (i != text.Length) throw new FormatException("JSON 末尾有多余内容 @" + i);
            return value;
        }

        public static Dictionary<string, object> AsObject(object o)
        {
            return o as Dictionary<string, object>;
        }

        public static List<object> AsArray(object o)
        {
            return o as List<object>;
        }

        public static string Str(Dictionary<string, object> o, string key)
        {
            if (o == null) return null;
            object v;
            if (!o.TryGetValue(key, out v) || v == null) return null;
            return v as string;
        }

        public static bool Bool(Dictionary<string, object> o, string key, bool def)
        {
            if (o == null) return def;
            object v;
            if (!o.TryGetValue(key, out v) || v == null) return def;
            if (v is bool) return (bool)v;
            return def;
        }

        public static Dictionary<string, object> Obj(Dictionary<string, object> o, string key)
        {
            if (o == null) return null;
            object v;
            if (!o.TryGetValue(key, out v)) return null;
            return v as Dictionary<string, object>;
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { i++; continue; }
                break;
            }
        }

        static object ParseValue(string s, ref int i, int depth)
        {
            if (depth > 64) throw new FormatException("JSON 嵌套超过 64 层");
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("JSON 缺少值");
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i, depth);
            if (c == '[') return ParseArray(s, ref i, depth);
            if (c == '"') return ParseString(s, ref i);
            if (c == 't') { Expect(s, ref i, "true"); return true; }
            if (c == 'f') { Expect(s, ref i, "false"); return false; }
            if (c == 'n') { Expect(s, ref i, "null"); return null; }
            return ParseNumber(s, ref i);
        }

        static void Expect(string s, ref int i, string lit)
        {
            if (i + lit.Length > s.Length || string.CompareOrdinal(s, i, lit, 0, lit.Length) != 0)
                throw new FormatException("JSON 解析失败：期望 " + lit + " @" + i);
            i += lit.Length;
        }

        static Dictionary<string, object> ParseObject(string s, ref int i, int depth)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            i++;
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (i < s.Length)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException("JSON 对象键必须以引号开始 @" + i);
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("JSON 缺少冒号 @" + i);
                i++;
                object val = ParseValue(s, ref i, depth + 1);
                d[key] = val;
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; return d; }
                throw new FormatException("JSON 对象未正确结束 @" + i);
            }
            throw new FormatException("JSON 对象不完整");
        }

        static List<object> ParseArray(string s, ref int i, int depth)
        {
            var list = new List<object>();
            i++;
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }
            while (i < s.Length)
            {
                object v = ParseValue(s, ref i, depth + 1);
                list.Add(v);
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; return list; }
                throw new FormatException("JSON 数组未正确结束 @" + i);
            }
            throw new FormatException("JSON 数组不完整");
        }

        static string ParseString(string s, ref int i)
        {
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c < 0x20) throw new FormatException("JSON 字符串含未转义控制字符");
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
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
                        int cp;
                        if (i + 4 > s.Length || !int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out cp))
                            throw new FormatException("JSON Unicode 转义无效");
                        sb.Append((char)cp);
                        i += 4;
                        break;
                    default: throw new FormatException("JSON 转义无效");
                }
            }
            throw new FormatException("JSON 字符串未结束");
        }

        static object ParseNumber(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && s[i] == '-') i++;
            if (i < s.Length && s[i] == '0') i++;
            else ReadDigits(s, ref i);
            if (i < s.Length && s[i] == '.') { i++; ReadDigits(s, ref i); }
            if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
            {
                i++;
                if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
                ReadDigits(s, ref i);
            }
            double d;
            if (double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out d) && !double.IsInfinity(d) && !double.IsNaN(d))
                return d;
            throw new FormatException("JSON 数字无效 @" + start);
        }

        static void ReadDigits(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
            if (start == i) throw new FormatException("JSON 缺少数字 @" + i);
        }

        /// <summary>把字符串转成 JSON 字符串字面量（含引号与转义）。</summary>
        public static string Quote(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
