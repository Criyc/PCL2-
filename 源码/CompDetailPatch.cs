using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

public static class CompDetailPatch
{
    public static void Apply(ModuleDefinition m)
    {
        var type = m.Types.Single(t => t.FullName == "PCL.PageDownloadResourceDetail");
        var method = type.Methods.Single(x => x.Name == "InitializeStackResourceVersions");
        var il = method.Body.GetILProcessor();
        var code = method.Body.Instructions;
        var save = type.Methods.Single(x => x.Name == "Save_Click");
        var install = type.Methods.Single(x => x.Name == "Install_Click");
        var pointer = code.Single(i => i.OpCode == OpCodes.Ldftn && ((MethodReference)i.Operand).Name == "Save_Click");
        var start = pointer.Previous;
        var listCtor = pointer.Next;
        var empty = listCtor.Next;
        var join = empty.Next;
        if (start.OpCode != OpCodes.Ldarg_0 || listCtor.OpCode != OpCodes.Newobj ||
            empty.OpCode != OpCodes.Ldnull || join.OpCode != OpCodes.Ldloc_1)
            throw new InvalidOperationException("Unexpected resource-list binding IL.");
        var iconCtor = m.Types.Single(t => t.FullName == "PCL.MyIconButton").NestedTypes
            .Single(t => t.Name == "ClickEventHandler").Methods.Single(x => x.IsConstructor);
        var field = type.Fields.Single(f => f.Name == "TargetResourceType");
        var oldCheck = code.Single(i => i.OpCode == OpCodes.Ldfld && ((FieldReference)i.Operand).FullName == field.FullName);
        if (oldCheck.Next.OpCode != OpCodes.Ldc_I4_2) throw new InvalidOperationException("Unexpected modpack resource type.");

        // Existing collection and ResourceVersion stay on the evaluation stack.
        var instructions = new[] {
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldfld, field),
            Instruction.Create(OpCodes.Ldc_I4_2),
            Instruction.Create(OpCodes.Bne_Un, start),
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldftn, install),
            Instruction.Create(OpCodes.Newobj, (MethodReference)listCtor.Operand),
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldftn, save),
            Instruction.Create(OpCodes.Newobj, iconCtor),
            Instruction.Create(OpCodes.Br, join)
        };
        foreach (var i in instructions) il.InsertBefore(start, i);
    }
}
