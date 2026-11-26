using System;
using System.IO;
using System.Reflection;

namespace ProxyPicker.App
{
    /// <summary>
    /// 单文件绿色便携加载器：从主 exe 嵌入资源动态加载自研分层 DLL
    /// （ProxyPicker.Domain / Platform.Windows / Infrastructure / Application），
    /// 开发期保持严格编译器分层边界，发布期保持真正单个 ProxyPicker.exe。
    /// </summary>
    internal static class EmbeddedAssemblyLoader
    {
        static bool _installed;

        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            AppDomain.CurrentDomain.AssemblyResolve += OnResolve;
        }

        static Assembly OnResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                var reqName = new AssemblyName(args.Name).Name;
                if (string.IsNullOrEmpty(reqName) || !reqName.StartsWith("ProxyPicker.", StringComparison.OrdinalIgnoreCase))
                    return null;

                string resName = "ProxyPicker.Libs." + reqName + ".dll";
                var self = Assembly.GetExecutingAssembly();
                using (Stream s = self.GetManifestResourceStream(resName))
                {
                    if (s == null) return null;
                    byte[] raw = new byte[s.Length];
                    int read = 0;
                    while (read < raw.Length)
                    {
                        int n = s.Read(raw, read, raw.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    return Assembly.Load(raw);
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
