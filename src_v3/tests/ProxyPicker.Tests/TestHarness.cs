using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace ProxyPicker.Tests
{
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class TestFactAttribute : Attribute
    {
        public string Name { get; }
        public TestFactAttribute(string name = null) { Name = name; }
    }

    internal sealed class TestAssertionException : Exception
    {
        public TestAssertionException(string msg) : base(msg) { }
    }

    internal static class AssertEx
    {
        public static void True(bool cond, string msg = "期望为 true")
        {
            if (!cond) throw new TestAssertionException(msg);
        }

        public static void False(bool cond, string msg = "期望为 false")
        {
            if (cond) throw new TestAssertionException(msg);
        }

        public static void Equal<T>(T expected, T actual, string msg = null)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new TestAssertionException((msg != null ? msg + " | " : "") + "期望=" + expected + " 实际=" + actual);
        }

        public static void NotEqual<T>(T notExpected, T actual, string msg = null)
        {
            if (EqualityComparer<T>.Default.Equals(notExpected, actual))
                throw new TestAssertionException((msg != null ? msg + " | " : "") + "不应等于 " + notExpected);
        }

        public static void Contains(string expectedSub, string actual, string msg = null)
        {
            if (actual == null || actual.IndexOf(expectedSub ?? "", StringComparison.Ordinal) < 0)
                throw new TestAssertionException((msg != null ? msg + " | " : "") + "未包含子串: " + expectedSub);
        }

        public static void DoesNotContain(string forbiddenSub, string actual, string msg = null)
        {
            if (actual != null && !string.IsNullOrEmpty(forbiddenSub) && actual.IndexOf(forbiddenSub, StringComparison.Ordinal) >= 0)
                throw new TestAssertionException((msg != null ? msg + " | " : "") + "不应包含子串: " + forbiddenSub);
        }

        public static void NotNull(object obj, string msg = "不应为 null")
        {
            if (obj == null) throw new TestAssertionException(msg);
        }

        public static void Null(object obj, string msg = "应为 null")
        {
            if (obj != null) throw new TestAssertionException(msg);
        }

        public static void Throws<TException>(Action act, string msg = null) where TException : Exception
        {
            try
            {
                act();
            }
            catch (TException)
            {
                return;
            }
            catch (Exception ex)
            {
                throw new TestAssertionException((msg != null ? msg + " | " : "") + "期望抛出 " + typeof(TException).Name + "，实际抛出 " + ex.GetType().Name);
            }
            throw new TestAssertionException((msg != null ? msg + " | " : "") + "期望抛出 " + typeof(TException).Name + "，但未抛出异常");
        }
    }

    internal static class TestRunnerMain
    {
        public static int Main(string[] args)
        {
            string filter = (args != null && args.Length > 0) ? (args[0] ?? "").Trim() : "";
            var asm = Assembly.GetExecutingAssembly();
            var methods = new List<Tuple<Type, MethodInfo, string>>();
            foreach (var t in asm.GetTypes().OrderBy(x => x.FullName, StringComparer.Ordinal))
            {
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                {
                    var attr = m.GetCustomAttribute<TestFactAttribute>();
                    if (attr == null) continue;
                    string label = string.IsNullOrEmpty(attr.Name) ? (t.Name + "." + m.Name) : (t.Name + "." + attr.Name);
                    if (filter.Length > 0 && label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    methods.Add(Tuple.Create(t, m, label));
                }
            }

            var swAll = Stopwatch.StartNew();
            int pass = 0, fail = 0;
            foreach (var item in methods)
            {
                var t = item.Item1;
                var m = item.Item2;
                var label = item.Item3;
                var sw = Stopwatch.StartNew();
                try
                {
                    object instance = m.IsStatic ? null : Activator.CreateInstance(t);
                    try
                    {
                        object ret = m.Invoke(instance, null);
                        if (ret is Task task) task.GetAwaiter().GetResult();
                    }
                    finally
                    {
                        if (instance is IDisposable d) d.Dispose();
                    }
                    pass++;
                    Console.WriteLine("PASS  [" + sw.ElapsedMilliseconds + "ms] " + label);
                }
                catch (TargetInvocationException tie)
                {
                    var inner = tie.InnerException ?? tie;
                    fail++;
                    Console.WriteLine("FAIL  [" + sw.ElapsedMilliseconds + "ms] " + label + " | " + inner.GetType().Name + ": " + inner.Message);
                }
                catch (Exception ex)
                {
                    fail++;
                    Console.WriteLine("FAIL  [" + sw.ElapsedMilliseconds + "ms] " + label + " | " + ex.GetType().Name + ": " + ex.Message);
                }
            }
            swAll.Stop();
            Console.WriteLine();
            Console.WriteLine("TOTAL=" + (pass + fail) + " PASS=" + pass + " FAIL=" + fail + " ELAPSED=" + swAll.ElapsedMilliseconds + "ms");
            return fail == 0 ? 0 : 1;
        }
    }
}
