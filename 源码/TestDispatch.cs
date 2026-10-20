using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Resources;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;
using CA = Mono.Cecil.MethodAttributes;

public static class TestDispatch
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            string root = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(root);
            string deps = Path.Combine(root, "deps");
            Directory.CreateDirectory(deps);
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(deps);
            string fixture = Path.Combine(root, "dispatch-fixture.exe");
            using (var m = ModuleDefinition.ReadModule(args[0], new ReaderParameters { AssemblyResolver = resolver }))
            {
                var resources = (EmbeddedResource)m.Resources.Single(r => r.Name == "PCL.Resources.resources");
                using (var reader = new ResourceReader(resources.GetResourceStream()))
                {
                    var e = reader.GetEnumerator();
                    while (e.MoveNext())
                    {
                        var bytes = e.Value as byte[];
                        if (bytes != null && bytes.Length > 2 && bytes[0] == 77 && bytes[1] == 90)
                            File.WriteAllBytes(Path.Combine(deps, (string)e.Key + ".dll"), bytes);
                    }
                }
                var t = m.Types.Single(x => x.FullName == "PCL.ModModpack");
                var selected = new FieldDefinition("TestSelected", Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static, m.TypeSystem.Int32);
                t.Fields.Add(selected);
                var names = new[] { "InstallPackCurseForge", "InstallPackHMCL", "InstallPackMMC", "InstallPackMCBBS", "InstallPackModrinth", "InstallPackLauncherPack", "InstallPackCompress" };
                for (int n = 0; n < names.Length; n++)
                {
                    var target = t.Methods.Single(x => x.Name == names[n]);
                    target.Body = new Mono.Cecil.Cil.MethodBody(target);
                    var il = target.Body.GetILProcessor();
                    il.Emit(OpCodes.Ldc_I4, n);
                    il.Emit(OpCodes.Stsfld, selected);
                    il.Emit(OpCodes.Ldnull);
                    il.Emit(OpCodes.Ret);
                }
                var method = t.Methods.Single(x => x.Name == "ModpackInstall" && x.Parameters.Count == 3);
                var logger = (MethodReference)method.Body.Instructions.First(i => i.Operand is MethodReference &&
                    ((MethodReference)i.Operand).FullName.Contains("MeloongCore.Logger::Info")).Operand;
                var silent = new MethodDefinition("TestSilentLogger", CA.Private | CA.Static, m.TypeSystem.Void);
                foreach (var p in logger.Parameters) silent.Parameters.Add(new ParameterDefinition(p.ParameterType));
                silent.Body.GetILProcessor().Emit(OpCodes.Ret);
                t.Methods.Add(silent);
                var open = (MethodReference)method.Body.Instructions.Single(i => i.Operand is MethodReference &&
                    ((MethodReference)i.Operand).Name == "OpenZip").Operand;
                var openTest = new MethodDefinition("TestOpenZip", CA.Private | CA.Static, open.ReturnType);
                foreach (var p in open.Parameters) openTest.Parameters.Add(new ParameterDefinition(p.ParameterType));
                var openIl = openTest.Body.GetILProcessor();
                openIl.Emit(OpCodes.Ldarg_0);
                openIl.Emit(OpCodes.Call, m.ImportReference(typeof(ZipFile).GetMethod("OpenRead", new[] { typeof(string) })));
                openIl.Emit(OpCodes.Ret);
                t.Methods.Add(openTest);
                foreach (var i in method.Body.Instructions)
                {
                    var call = i.Operand as MethodReference;
                    if (call == null) continue;
                    if (call.FullName == logger.FullName) i.Operand = silent;
                    else if (call.FullName == open.FullName) i.Operand = openTest;
                    else if (call.Name == "get_McFolderSelected") { i.OpCode = OpCodes.Ldstr; i.Operand = root + "\\"; }
                }
                m.Write(fixture);
            }
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e)
            {
                string path = Path.Combine(deps, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
            var assembly = Assembly.LoadFrom(fixture);
            var type = assembly.GetType("PCL.ModModpack", true);
            var dispatch = type.GetMethod("ModpackInstall", new[] { typeof(string), typeof(string), typeof(string) });
            var result = type.GetField("TestSelected");
            var cases = new[] {
                new[] {"curseforge", "manifest.json", "{\"minecraft\":{\"version\":\"1.21.1\"}}", "0"},
                new[] {"hmcl", "modpack.json", "{}", "1"},
                new[] {"mmc", "mmc-pack.json", "{}", "2"},
                new[] {"mcbbs", "mcbbs.packmeta", "{}", "3"},
                new[] {"mcbbs-addons", "manifest.json", "{\"addons\":[]}", "3"},
                new[] {"modrinth", "modrinth.index.json", "{}", "4"},
                new[] {"launcher", "modpack.zip", "placeholder", "5"},
                new[] {"launcher-mrpack", "modpack.mrpack", "placeholder", "5"},
                new[] {"generic", "readme.txt", "fixture", "6"},
                new[] {"nested-cf", "folder/manifest.json", "{}", "0"},
                new[] {"nested-hmcl", "folder/modpack.json", "{}", "1"},
                new[] {"nested-mmc", "folder/mmc-pack.json", "{}", "2"},
                new[] {"nested-mcbbs", "folder/mcbbs.packmeta", "{}", "3"},
                new[] {"nested-modrinth", "folder/modrinth.index.json", "{}", "4"},
                new[] {"nested-launcher", "folder/modpack.mrpack", "fixture", "5"}
            };
            foreach (var item in cases)
            {
                string zip = Path.Combine(root, item[0] + ".zip");
                using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                using (var writer = new StreamWriter(archive.CreateEntry(item[1]).Open()))
                    writer.Write(item[2]);
                result.SetValue(null, -1);
                dispatch.Invoke(null, new object[] { zip, "fixture-instance", null });
                if ((int)result.GetValue(null) != Int32.Parse(item[3])) throw new Exception("Incorrect dispatch: " + item[0]);
                using (File.Open(zip, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                Console.WriteLine("DISPATCH_AND_DISPOSE_OK: " + item[0]);
            }
            Console.WriteLine("TEST_OK: 15 archive-recognition/dispatch cases; installer bodies stubbed, no game files installed.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
