using System;

namespace ProxyPicker.Domain.Services
{
    /// <summary>语义版本号比较与发布链接白名单校验。</summary>
    public static class VersionComparer
    {
        public static bool IsNewer(string candidate, string current)
        {
            int[] a, b;
            if (!TryParse(candidate, out a) || !TryParse(current, out b)) return false;
            int n = Math.Max(a.Length, b.Length);
            for (int i = 0; i < n; i++)
            {
                int va = i < a.Length ? a[i] : 0;
                int vb = i < b.Length ? b[i] : 0;
                if (va != vb) return va > vb;
            }
            return false;
        }

        static bool TryParse(string raw, out int[] parts)
        {
            parts = null;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            string s = raw.Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
            int dash = s.IndexOf('-');
            if (dash >= 0) s = s.Substring(0, dash);
            string[] segs = s.Split('.');
            if (segs.Length == 0) return false;
            parts = new int[segs.Length];
            for (int i = 0; i < segs.Length; i++)
            {
                int v;
                if (!int.TryParse(segs[i], out v) || v < 0) return false;
                parts[i] = v;
            }
            return true;
        }

        public static string CleanTag(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string s = raw.Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
            if (s.StartsWith("release-", StringComparison.OrdinalIgnoreCase)) s = s.Substring(8);
            int cut = s.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut > 0) s = s.Substring(0, cut);
            return s.Trim();
        }

        public static bool IsSafeReleaseUrl(string url, string repoSlug)
        {
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(repoSlug)) return false;
            Uri u;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out u)) return false;
            if (!string.Equals(u.Scheme, "https", StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(u.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return false;
            string prefix = "/" + repoSlug.Trim('/');
            return u.AbsolutePath.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                   u.AbsolutePath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
        }
    }
}
