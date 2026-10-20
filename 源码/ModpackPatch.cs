using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

public static class ModpackPatch
{
    public static void Apply(ModuleDefinition m)
    {
        var type = m.Types.Single(t => t.FullName == "PCL.ModModpack");
        RestoreDispatch(type);
        RestoreDialog(m, type);
    }

    private static void RestoreDispatch(TypeDefinition type)
    {
        var method = type.Methods.Single(x => x.Name == "ModpackInstall" && x.Parameters.Count == 3);
        var body = method.Body;
        var il = body.GetILProcessor();
        var blocked = body.Instructions.Single(i => i.OpCode == OpCodes.Ldstr && i.Operand.ToString().Contains("不再提供 MC 下载"));
        var ctor = blocked.Next;
        var throwing = ctor.Next;
        if (ctor.OpCode != OpCodes.Newobj || throwing.OpCode != OpCodes.Throw ||
            body.Variables[4].VariableType.FullName != "System.Int32" ||
            !body.Variables[0].VariableType.FullName.Contains("LoaderCombo"))
            throw new InvalidOperationException("Unexpected modpack dispatch IL.");
        var outer = body.ExceptionHandlers.Single(e => e.HandlerType == ExceptionHandlerType.Finally && e.HandlerEnd == null);
        var finallyStart = throwing.Next;
        if (outer.HandlerStart != finallyStart || outer.TryEnd != finallyStart)
            throw new InvalidOperationException("Unexpected modpack finally boundary.");
        blocked.OpCode = OpCodes.Nop;
        blocked.Operand = null;
        il.Remove(ctor);
        il.Remove(throwing);
        var returned = Instruction.Create(OpCodes.Ldloc_0);
        outer.HandlerEnd = returned;
        il.Append(returned);
        il.Append(Instruction.Create(OpCodes.Ret));

        var labels = Enumerable.Range(0, 7).Select(x => Instruction.Create(OpCodes.Nop)).ToArray();
        var code = new List<Instruction>();
        code.Add(Instruction.Create(OpCodes.Ldloc, body.Variables[4]));
        code.Add(Instruction.Create(OpCodes.Switch, labels.Take(5).ToArray()));
        code.Add(Instruction.Create(OpCodes.Ldloc, body.Variables[4]));
        code.Add(Instruction.Create(OpCodes.Ldc_I4, 9));
        code.Add(Instruction.Create(OpCodes.Beq, labels[5]));
        code.Add(Instruction.Create(OpCodes.Br, labels[6]));
        var names = new[] { "InstallPackCurseForge", "InstallPackHMCL", "InstallPackMMC", "InstallPackMCBBS", "InstallPackModrinth", "InstallPackLauncherPack", "InstallPackCompress" };
        var descriptions = new[] { "CurseForge", "HMCL", "MMC", "MCBBS", "Modrinth", "带启动器的压缩包", "未能识别，假定为压缩包" };
        var logger = (MethodReference)body.Instructions.First(i => i.Operand is MethodReference &&
            ((MethodReference)i.Operand).FullName.Contains("MeloongCore.Logger::Info")).Operand;
        for (int n = 0; n < names.Length; n++)
        {
            var target = type.Methods.Single(x => x.Name == names[n]);
            code.Add(labels[n]);
            code.Add(Instruction.Create(OpCodes.Ldstr, "整合包种类：" + descriptions[n]));
            code.Add(Instruction.Create(OpCodes.Ldc_I4_0));
            code.Add(Instruction.Create(OpCodes.Ldstr, "LocalDownloadRestore/ModModpack.vb"));
            code.Add(Instruction.Create(OpCodes.Call, logger));
            code.Add(Instruction.Create(OpCodes.Ldarg_0));
            code.Add(Instruction.Create(OpCodes.Ldloc_1));
            if (target.Parameters.Count >= 3) code.Add(Instruction.Create(OpCodes.Ldloc_2));
            if (target.Parameters.Count >= 4) code.Add(Instruction.Create(OpCodes.Ldarg_1));
            if (target.Parameters.Count >= 5) code.Add(Instruction.Create(OpCodes.Ldarg_2));
            if (target.Parameters.Count < 2 || target.Parameters.Count > 5)
                throw new InvalidOperationException("Unexpected installer parameters.");
            code.Add(Instruction.Create(OpCodes.Call, target));
            code.Add(Instruction.Create(OpCodes.Stloc_0));
            code.Add(Instruction.Create(OpCodes.Leave, returned));
        }
        foreach (var i in code) il.InsertBefore(finallyStart, i);
    }

    private static void RestoreDialog(ModuleDefinition m, TypeDefinition type)
    {
        var method = type.Methods.Single(x => x.Name == "ModpackInstall" && x.Parameters.Count == 0);
        if (method.Body.Instructions.Count != 7) throw new InvalidOperationException("Unexpected modpack dialog IL.");
        var closure = type.NestedTypes.Single(t => t.Name == "_Closure$__0-0");
        var file = closure.Fields.Single(f => f.Name == "$VB$Local_File");
        var background = closure.Methods.Single(x => x.Name == "_Lambda$__0");
        var referenceBody = m.Types.Single(t => t.FullName == "PCL.ModJava").Methods.Single(x => x.Name == "ManuallyImportJava").Body;
        var existing = referenceBody.Instructions;
        var select = existing.Single(i => i.Operand is MethodReference && ((MethodReference)i.Operand).Name == "SelectFile");
        var convertFilter = (MethodReference)select.Previous.Previous.Operand;
        var nullableCtor = (MethodReference)select.Previous.Operand;
        if (convertFilter.Name != "op_Implicit" || convertFilter.Parameters[0].ParameterType.FullName != "!1")
            throw new InvalidOperationException("Unexpected dialog filter converter.");
        var first = (MethodReference)select.Next.Operand;
        var isEmpty = (MethodReference)existing.First(i => i.Operand is MethodReference &&
            ((MethodReference)i.Operand).Name == "IsNullOrEmpty").Operand;
        var actionCtor = m.GetTypes().SelectMany(t => t.Methods).Where(x => x.HasBody)
            .SelectMany(x => x.Body.Instructions).Where(i => i.OpCode == OpCodes.Newobj && i.Operand is MethodReference)
            .Select(i => (MethodReference)i.Operand).First(x => x.DeclaringType.FullName == "System.Action" && x.Name == ".ctor");
        var run = m.Types.Single(t => t.FullName == "PCL.ModBase").Methods.Single(x => x.Name == "RunInThread" &&
            x.Parameters.Count == 1 && x.Parameters[0].ParameterType.FullName == "System.Action");

        method.Body = new MethodBody(method);
        method.Body.InitLocals = true;
        method.Body.Variables.Add(new VariableDefinition(closure));
        var il = method.Body.GetILProcessor();
        var end = Instruction.Create(OpCodes.Ret);
        il.Emit(OpCodes.Newobj, closure.Methods.Single(x => x.IsConstructor));
        il.Emit(OpCodes.Stloc_0);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Ldstr, "选择整合包压缩文件");
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ldstr, "整合包文件|*.rar;*.zip;*.mrpack");
        il.Emit(OpCodes.Call, convertFilter);
        il.Emit(OpCodes.Newobj, nullableCtor);
        il.Emit(OpCodes.Call, (MethodReference)select.Operand);
        il.Emit(OpCodes.Call, first);
        il.Emit(OpCodes.Stfld, file);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Ldfld, file);
        il.Emit(OpCodes.Call, isEmpty);
        il.Emit(OpCodes.Brtrue, end);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Ldftn, background);
        il.Emit(OpCodes.Newobj, actionCtor);
        il.Emit(OpCodes.Call, run);
        il.Append(end);
    }
}
