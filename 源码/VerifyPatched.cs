using System;
using System.IO;
using System.Reflection;
using System.Resources;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

[assembly: AssemblyDescription("Local distribution watermark: Cricy")]

public static class VerifyPatched
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            var embedded = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var loaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e)
            {
                string name = new AssemblyName(e.Name).Name;
                Assembly existing;
                if (loaded.TryGetValue(name, out existing)) return existing;
                byte[] bytes;
                if (!embedded.TryGetValue(name, out bytes)) return null;
                existing = Assembly.Load(bytes);
                loaded[name] = existing;
                return existing;
            };
            var assembly = Assembly.LoadFile(Path.GetFullPath(args[0]));
            using (var reader = new ResourceReader(assembly.GetManifestResourceStream("PCL.Resources.resources")))
            {
                var e = reader.GetEnumerator();
                while (e.MoveNext())
                {
                    var bytes = e.Value as byte[];
                    if (bytes != null && bytes.Length > 2 && bytes[0] == 77 && bytes[1] == 90) embedded[(string)e.Key] = bytes;
                }
            }
            string[] types = { "PCL.PageDownloadInstall", "PCL.PageDownloadModify", "PCL.ModModpack", "PCL.PageDownloadResourceDetail" };
            string[] methods = { "LoaderInit", "LoadInstanceFinished", "ModpackInstall", "InitializeStackResourceVersions" };
            int count = 0;
            for (int n = 0; n < types.Length; n++)
            {
                var type = assembly.GetType(types[n], true);
                foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (method.Name != methods[n]) continue;
                    RuntimeHelpers.PrepareMethod(method.MethodHandle);
                    Console.WriteLine("JIT_OK: " + type.FullName + "." + method);
                    count++;
                }
            }
            if (count != 5) throw new Exception("Expected five restored methods.");
            Console.WriteLine("VERIFY_OK: all five restored methods accepted by .NET Framework JIT; no UI or download was executed.");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 1;
        }
    }
}
