using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Resources;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;

[assembly: System.Reflection.AssemblyDescription("Local distribution watermark: Cricy")]

public static class PclRestore
{
    public const string Marker = "PCL.LocalDownloadRestore.v1";

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--inspect")
            {
                using (var inspected = ModuleDefinition.ReadModule(Path.GetFullPath(args[1])))
                {
                    if (inspected.Assembly.Name.Name != "Plain Craft Launcher 2" || inspected.Assembly.Name.Version.ToString() != "2.99.0.0")
                        throw new InvalidOperationException("Unsupported launcher identity or version; expected PCL 2.99.0.0.");
                    if (inspected.Resources.Any(r => r.Name == Marker))
                    {
                        Validate(inspected);
                        Console.WriteLine("ALREADY_PATCHED");
                        return 10;
                    }
                    var flag = inspected.Types.Single(t => t.FullName == "PCL.ModSecret").Fields.Single(f => f.Name == "RestrictMcDownload");
                    if (!(flag.Constant is bool) || !(bool)flag.Constant)
                        throw new InvalidOperationException("No supported download restriction found.");
                    Console.WriteLine("SUPPORTED_ORIGINAL: PCL 2.99.0.0. Full structural checks will run before installation.");
                    return 0;
                }
            }
            if (args.Length != 2) throw new ArgumentException("Usage: PclRestore.exe input.exe output.exe");
            string input = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
            if (String.Equals(input, output, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Input and output must be different files.");
            if (File.Exists(output)) throw new IOException("Output already exists; refusing to overwrite.");
            string originalHash;
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(input))
                originalHash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");

            using (var resolver = new EmbeddedResolver())
            using (var module = ModuleDefinition.ReadModule(input, new ReaderParameters { InMemory = true, AssemblyResolver = resolver }))
            {
                resolver.LoadEmbedded(module);
                if (module.Assembly.Name.Name != "Plain Craft Launcher 2" || module.Assembly.Name.Version.ToString() != "2.99.0.0")
                    throw new InvalidOperationException("Unexpected assembly identity or version.");
                if (module.Resources.Any(r => r.Name == Marker)) throw new InvalidOperationException("Input already patched.");
                PatchVanilla(module);
                PatchModify(module);
                ModpackPatch.Apply(module);
                CompDetailPatch.Apply(module);
                var constant = module.Types.Single(t => t.FullName == "PCL.ModSecret").Fields.Single(f => f.Name == "RestrictMcDownload");
                if (!(constant.Constant is bool) || !(bool)constant.Constant) throw new InvalidOperationException("Unexpected restriction constant.");
                constant.Constant = false;
                foreach (var type in module.GetTypes())
                    foreach (var method in type.Methods)
                        if (method.HasBody &&
                            ((type.FullName == "PCL.PageDownloadInstall" && method.Name == "LoaderInit") ||
                             (type.FullName == "PCL.PageDownloadModify" && method.Name == "LoadInstanceFinished") ||
                             (type.FullName == "PCL.ModModpack" && method.Name == "ModpackInstall") ||
                             (type.FullName == "PCL.PageDownloadResourceDetail" && method.Name == "InitializeStackResourceVersions")))
                            ExpandBranches(method);
                Validate(module);
                module.Resources.Add(new EmbeddedResource(Marker, ManifestResourceAttributes.Private,
                    Encoding.UTF8.GetBytes("Local reversible restoration of Minecraft downloads, modpack installation and version editing. Original SHA256: " + originalHash)));
                module.Write(output);
            }
            using (var saved = ModuleDefinition.ReadModule(output))
            {
                Validate(saved);
                if (!saved.Resources.Any(r => r.Name == Marker)) throw new InvalidOperationException("Patch marker missing.");
            }
            Console.WriteLine("PATCH_OK: 2.99.0.0 vanilla, modpack and version-editing entries restored.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("PATCH_FAILED: " + ex.ToString());
            return 1;
        }
    }

    private sealed class EmbeddedResolver : DefaultAssemblyResolver
    {
        private readonly Dictionary<string, byte[]> data = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        public void LoadEmbedded(ModuleDefinition module)
        {
            foreach (var resource in module.Resources.OfType<EmbeddedResource>().Where(r => r.Name == "PCL.Resources.resources"))
            using (var reader = new ResourceReader(resource.GetResourceStream()))
            {
                var e = reader.GetEnumerator();
                while (e.MoveNext())
                {
                    var bytes = e.Value as byte[];
                    if (bytes != null && bytes.Length > 2 && bytes[0] == 0x4d && bytes[1] == 0x5a)
                        data[(string)e.Key] = bytes;
                }
            }
        }
        public override AssemblyDefinition Resolve(AssemblyNameReference name)
        {
            byte[] bytes;
            if (data.TryGetValue(name.Name, out bytes))
            {
                var assembly = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes),
                    new ReaderParameters { InMemory = true, AssemblyResolver = this });
                RegisterAssembly(assembly);
                return assembly;
            }
            return base.Resolve(name);
        }
    }

    private static void PatchVanilla(ModuleDefinition module)
    {
        var page = module.Types.Single(t => t.FullName == "PCL.PageDownloadInstall");
        var method = page.Methods.Single(m => m.Name == "LoaderInit");
        var code = method.Body.Instructions;
        var first = code.Single(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "关闭下载");
        var last = code.Single(i => i.OpCode == OpCodes.Newobj && i.Operand.ToString().Contains("LoaderTask`2") &&
            i.Operand.ToString().Contains("System.Threading.ThreadPriority"));
        int a = code.IndexOf(first), b = code.IndexOf(last);
        if (b <= a || method.Body.ExceptionHandlers.Count != 0 || code.Count != 30)
            throw new InvalidOperationException("Unexpected vanilla loader IL.");
        var field = module.Types.Single(t => t.FullName == "PCL.ModDownload").Fields.Single(f => f.Name == "DlClientListLoader");
        first.OpCode = OpCodes.Ldsfld;
        first.Operand = field;
        for (int n = b; n > a; n--) code.RemoveAt(n);
    }

    private static void PatchModify(ModuleDefinition module)
    {
        var method = module.Types.Single(t => t.FullName == "PCL.PageDownloadModify").Methods.Single(m => m.Name == "LoadInstanceFinished");
        var code = method.Body.Instructions;
        var getter = code.Single(i => i.Operand is MethodReference && ((MethodReference)i.Operand).Name == "get_CardMinecraft");
        int n = code.IndexOf(getter);
        if (code[n+1].OpCode != OpCodes.Ldc_I4_2 || !code[n+2].Operand.ToString().Contains("set_Visibility"))
            throw new InvalidOperationException("Unexpected Minecraft version-card visibility IL.");
        code[n+1].OpCode = OpCodes.Ldc_I4_0;
    }

    private static void ExpandBranches(MethodDefinition method)
    {
        foreach (var i in method.Body.Instructions)
        {
            switch (i.OpCode.Code)
            {
                case Code.Br_S: i.OpCode = OpCodes.Br; break;
                case Code.Brfalse_S: i.OpCode = OpCodes.Brfalse; break;
                case Code.Brtrue_S: i.OpCode = OpCodes.Brtrue; break;
                case Code.Beq_S: i.OpCode = OpCodes.Beq; break;
                case Code.Bge_S: i.OpCode = OpCodes.Bge; break;
                case Code.Bge_Un_S: i.OpCode = OpCodes.Bge_Un; break;
                case Code.Bgt_S: i.OpCode = OpCodes.Bgt; break;
                case Code.Bgt_Un_S: i.OpCode = OpCodes.Bgt_Un; break;
                case Code.Ble_S: i.OpCode = OpCodes.Ble; break;
                case Code.Ble_Un_S: i.OpCode = OpCodes.Ble_Un; break;
                case Code.Blt_S: i.OpCode = OpCodes.Blt; break;
                case Code.Blt_Un_S: i.OpCode = OpCodes.Blt_Un; break;
                case Code.Bne_Un_S: i.OpCode = OpCodes.Bne_Un; break;
                case Code.Leave_S: i.OpCode = OpCodes.Leave; break;
            }
        }
    }

    private static void Validate(ModuleDefinition module)
    {
        foreach (var type in module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                var code = method.Body.Instructions;
                foreach (var i in code)
                {
                    var target = i.Operand as Instruction;
                    if (target != null && !code.Contains(target)) throw new InvalidOperationException("Dangling branch in " + method.FullName);
                    var targets = i.Operand as Instruction[];
                    if (targets != null && targets.Any(t => !code.Contains(t))) throw new InvalidOperationException("Dangling switch in " + method.FullName);
                }
                foreach (var eh in method.Body.ExceptionHandlers)
                    foreach (var boundary in new [] { eh.TryStart, eh.TryEnd, eh.HandlerStart, eh.HandlerEnd, eh.FilterStart })
                        if (boundary != null && !code.Contains(boundary)) throw new InvalidOperationException("Dangling exception boundary in " + method.FullName);
            }
        }
        var vanilla = module.Types.Single(t => t.FullName == "PCL.PageDownloadInstall").Methods.Single(m => m.Name == "LoaderInit");
        if (!vanilla.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldsfld && i.Operand.ToString().Contains("DlClientListLoader")))
            throw new InvalidOperationException("Vanilla loader not restored.");
        foreach (var method in module.Types.Single(t => t.FullName == "PCL.ModModpack").Methods.Where(m => m.Name == "ModpackInstall"))
            if (method.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && i.Operand.ToString().Contains("不再提供 MC 下载")))
                throw new InvalidOperationException("Modpack restriction still present.");
    }
}
