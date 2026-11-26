using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ProxyPicker.Domain.Json
{
    /// <summary>
    /// 零外部依赖的安全 JSON 解析与序列化编解码器（支持完整 Unicode 代理对、大整数与受控递归深度）。
    /// </summary>
    public static class JsonCodec
    {
        const int MaxDepth = 64;

        public static object Parse(string text)
        {
            if (text == null) return null;
            int idx = 0;
            SkipWs(text, ref idx);
            if (idx >= text.Length) return null;
            object val = ParseValue(text, ref idx, 0);
            SkipWs(text, ref idx);
            if (idx < text.Length)
                throw new FormatException("JSON 末尾存在多余内容（位置 " + idx + "）");
            return val;
        }

        public static Dictionary<string, object> ParseObject(string text)
        {
            return AsObject(Parse(text));
        }

        public static Dictionary<string, object> AsObject(object v)
        {
            return v as Dictionary<string, object>;
        }

        public static List<object> AsArray(object v)
        {
            return v as List<object>;
        }

        public static Dictionary<string, object> Obj(Dictionary<string, object> d, string key)
        {
            object v;
            return (d != null && d.TryGetValue(key, out v)) ? v as Dictionary<string, object> : null;
        }

        public static List<object> Arr(Dictionary<string, object> d, string key)
        {
            object v;
            return (d != null && d.TryGetValue(key, out v)) ? v as List<object> : null;
        }

        public static string Str(Dictionary<string, object> d, string key, string fallback = "")
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return fallback;
            var s = v as string;
            return s ?? Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static bool Bool(Dictionary<string, object> d, string key, bool fallback = false)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return fallback;
            if (v is bool b) return b;
            if (v is string s)
            {
                if (bool.TryParse(s, out bool parsed)) return parsed;
            }
            return fallback;
        }

        public static int Int(Dictionary<string, object> d, string key, int fallback = 0)
        {
            long lv = Long(d, key, fallback);
            if (lv < int.MinValue || lv > int.MaxValue) return fallback;
            return (int)lv;
        }

        public static long Long(Dictionary<string, object> d, string key, long fallback = 0)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return fallback;
            if (v is long l) return l;
            if (v is int i) return i;
            if (v is double db) return (long)db;
            if (v is string s && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
                return parsed;
            return fallback;
        }

        public static string Quote(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
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
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static string Stringify(object value, bool indented = false)
        {
            var sb = new StringBuilder(256);
            WriteValue(sb, value, indented, 0);
            return sb.ToString();
        }

        static void WriteValue(StringBuilder sb, object value, bool indented, int depth)
        {
            if (value == null) { sb.Append("null"); return; }
            if (value is string s) { sb.Append(Quote(s)); return; }
            if (value is bool b) { sb.Append(b ? "true" : "false"); return; }
            if (value is int i) { sb.Append(i.ToString(CultureInfo.InvariantCulture)); return; }
            if (value is long l) { sb.Append(l.ToString(CultureInfo.InvariantCulture)); return; }
            if (value is double db)
            {
                if (double.IsNaN(db) || double.IsInfinity(db)) sb.Append("0");
                else sb.Append(db.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (value is IDictionary<string, object> dict)
            {
                sb.Append('{');
                bool first = true;
                foreach (var kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    if (indented) { sb.Append("\r\n"); AppendIndent(sb, depth + 1); }
                    sb.Append(Quote(kv.Key));
                    sb.Append(indented ? ": " : ":");
                    WriteValue(sb, kv.Value, indented, depth + 1);
                }
                if (!first && indented) { sb.Append("\r\n"); AppendIndent(sb, depth); }
                sb.Append('}');
                return;
            }
            if (value is System.Collections.IEnumerable seq)
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in seq)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    if (indented) { sb.Append("\r\n"); AppendIndent(sb, depth + 1); }
                    WriteValue(sb, item, indented, depth + 1);
                }
                if (!first && indented) { sb.Append("\r\n"); AppendIndent(sb, depth); }
                sb.Append(']');
                return;
            }
            sb.Append(Quote(Convert.ToString(value, CultureInfo.InvariantCulture)));
        }

        static void AppendIndent(StringBuilder sb, int depth)
        {
            for (int i = 0; i < depth; i++) sb.Append("  ");
        }

        static object ParseValue(string s, ref int i, int depth)
        {
            if (depth > MaxDepth) throw new FormatException("JSON 嵌套层数超过安全上限");
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("JSON 意外结束");
            char c = s[i];
            if (c == '{') return ParseObjectCore(s, ref i, depth + 1);
            if (c == '[') return ParseArrayCore(s, ref i, depth + 1);
            if (c == '"') return ParseString(s, ref i);
            if (c == 't' && MatchWord(s, ref i, "true")) return true;
            if (c == 'f' && MatchWord(s, ref i, "false")) return false;
            if (c == 'n' && MatchWord(s, ref i, "null")) return null;
            if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber(s, ref i);
            throw new FormatException("无法识别的 JSON 字符 '" + c + "'（位置 " + i + "）");
        }

        static Dictionary<string, object> ParseObjectCore(string s, ref int i, int depth)
        {
            var dict = new Dictionary<string, object>(StringComparer.Ordinal);
            i++; // '{'
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return dict; }
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException("JSON 对象键名必须是字符串（位置 " + i + "）");
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("JSON 缺少冒号（位置 " + i + "）");
                i++;
                dict[key] = ParseValue(s, ref i, depth);
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("JSON 对象未闭合");
                if (s[i] == '}') { i++; return dict; }
                if (s[i] != ',') throw new FormatException("JSON 对象缺少逗号（位置 " + i + "）");
                i++;
            }
        }

        static List<object> ParseArrayCore(string s, ref int i, int depth)
        {
            var list = new List<object>();
            i++; // '['
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }
            while (true)
            {
                list.Add(ParseValue(s, ref i, depth));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("JSON 数组未闭合");
                if (s[i] == ']') { i++; return list; }
                if (s[i] != ',') throw new FormatException("JSON 数组缺少逗号（位置 " + i + "）");
                i++;
            }
        }

        static string ParseString(string s, ref int i)
        {
            i++; // '"'
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    if (i >= s.Length) throw new FormatException("JSON 转义未完成");
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
                            if (i + 4 > s.Length) throw new FormatException("JSON \\u 转义长度不足");
                            string hex = s.Substring(i, 4);
                            i += 4;
                            sb.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            break;
                        default:
                            throw new FormatException("非法 JSON 转义符 \\" + e);
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            throw new FormatException("JSON 字符串未闭合");
        }

        static object ParseNumber(string s, ref int i)
        {
            int start = i;
            bool isFloat = false;
            if (s[i] == '-') i++;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
            if (i < s.Length && s[i] == '.')
            {
                isFloat = true;
                i++;
                while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
            }
            if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
            {
                isFloat = true;
                i++;
                if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
                while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
            }
            string raw = s.Substring(start, i - start);
            if (!isFloat && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long lv))
                return lv;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double dv))
                return dv;
            throw new FormatException("非法 JSON 数字: " + raw);
        }

        static bool MatchWord(string s, ref int i, string word)
        {
            if (i + word.Length > s.Length) return false;
            for (int k = 0; k < word.Length; k++)
                if (s[i + k] != word[k]) return false;
            i += word.Length;
            return true;
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }
    }
}
