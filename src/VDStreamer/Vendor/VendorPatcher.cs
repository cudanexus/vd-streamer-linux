using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace VDStreamer.Vendor;

/// <summary>
/// Removes the three macOS-only code paths from the vendor's VirtualDesktop.Net.dll so the
/// otherwise portable networking library runs on Linux. The patch sites were identified with
/// REA (inspect_managed_native_boundaries + inspect_managed_members): NetworkInterfaceHelper..cctor
/// (CFString.CreateNative), GetActiveAdapters (SystemConfiguration) and IsUsbAdapter (IOKit).
/// </summary>
internal static class VendorPatcher
{
    public const string PatchMarker = "VDStreamer.Linux.Patched";

    public static IReadOnlyList<string> PatchNetAssembly(string path)
    {
        var log = new List<string>();
        byte[] original = File.ReadAllBytes(path);
        using var input = new MemoryStream(original);
        using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { InMemory = true });
        ModuleDefinition module = assembly.MainModule;

        if (module.Resources.Any(r => r.Name == PatchMarker))
        {
            log.Add("VirtualDesktop.Net.dll already patched");
            return log;
        }

        TypeDefinition helper = module.GetType("VirtualDesktop.Net.NetworkInterfaceHelper")
            ?? throw new InvalidDataException("NetworkInterfaceHelper not found; unsupported vendor version");

        // 1. Static constructor: CFBundleIdentifierKernel = (nint)CFString.CreateNative("CFBundleIdentifierKernel")
        //    -> CFBundleIdentifierKernel = IntPtr.Zero
        MethodDefinition cctor = helper.Methods.FirstOrDefault(m => m.IsConstructor && m.IsStatic);
        if (cctor != null)
        {
            var instructions = cctor.Body.Instructions;
            for (int i = 0; i < instructions.Count; i++)
            {
                Instruction ins = instructions[i];
                if (ins.OpCode == OpCodes.Stsfld && ins.Operand is FieldReference field && field.Name == "CFBundleIdentifierKernel")
                {
                    int start = i - 1;
                    while (start >= 0 && instructions[start].OpCode != OpCodes.Ldstr)
                    {
                        start--;
                    }
                    if (start < 0)
                    {
                        throw new InvalidDataException("Unexpected NetworkInterfaceHelper..cctor layout");
                    }
                    for (int j = start; j < i; j++)
                    {
                        instructions[j].OpCode = OpCodes.Nop;
                        instructions[j].Operand = null;
                    }
                    instructions[start].OpCode = OpCodes.Ldc_I4_0;
                    instructions[start + 1].OpCode = OpCodes.Conv_I;
                    log.Add("Patched NetworkInterfaceHelper..cctor (CFString.CreateNative)");
                    break;
                }
            }
        }

        // 2. GetActiveAdapters(): SystemConfiguration enumeration -> return null (callers handle null)
        ReplaceBody(helper, "GetActiveAdapters", il => il.Emit(OpCodes.Ldnull), log);

        // 3. IsUsbAdapter(string): IOKit registry walk -> return false
        ReplaceBody(helper, "IsUsbAdapter", il => il.Emit(OpCodes.Ldc_I4_0), log);

        // Verify no remaining code path touches the Xamarin macOS bindings.
        var remaining = new List<string>();
        foreach (TypeDefinition type in module.GetTypes())
        {
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
            {
                foreach (Instruction ins in method.Body.Instructions)
                {
                    if (ins.Operand is MemberReference mr && IsMacOSScope(mr.DeclaringType?.Scope))
                    {
                        remaining.Add(type.FullName + "::" + method.Name + " -> " + mr.FullName);
                    }
                }
            }
        }
        if (remaining.Count > 0)
        {
            throw new InvalidDataException("VirtualDesktop.Net still references macOS APIs:\n" + string.Join("\n", remaining));
        }

        module.Resources.Add(new EmbeddedResource(PatchMarker, ManifestResourceAttributes.Private, Array.Empty<byte>()));
        string tmp = path + ".tmp";
        assembly.Write(tmp);
        File.Move(tmp, path, overwrite: true);
        log.Add("Wrote patched " + Path.GetFileName(path));
        return log;
    }

    private static bool IsMacOSScope(IMetadataScope scope) =>
        scope is AssemblyNameReference a && a.Name == "Microsoft.macOS";

    private static void ReplaceBody(TypeDefinition type, string name, Action<ILProcessor> emitResult, List<string> log)
    {
        MethodDefinition method = type.Methods.FirstOrDefault(m => m.Name == name)
            ?? throw new InvalidDataException(type.FullName + "::" + name + " not found; unsupported vendor version");
        MethodBody body = method.Body;
        body.Instructions.Clear();
        body.ExceptionHandlers.Clear();
        body.Variables.Clear();
        body.InitLocals = false;
        ILProcessor il = body.GetILProcessor();
        emitResult(il);
        il.Emit(OpCodes.Ret);
        log.Add("Patched " + type.Name + "::" + name);
    }
}
