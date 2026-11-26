using System;
using System.Runtime.CompilerServices;

namespace ProxyPicker.App
{
    internal static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // 首行安装单文件嵌入程序集解析器，必须在 JIT 进入任何引用 Domain/Application 类型的方法前执行
            EmbeddedAssemblyLoader.Install();
            RunCore(args ?? new string[0]);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void RunCore(string[] args)
        {
            WpfBootstrap.Run(args);
        }
    }
}
