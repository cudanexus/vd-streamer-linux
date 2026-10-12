using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace VDStreamer.Vendor;

/// <summary>
/// The vendor assemblies were trimmed (ILLink) together with the macOS copy of the .NET base
/// library. The trimmer removed interface/abstract members that the macOS app never called and
/// also removed them from the bundled BCL, so the result is only consistent with that trimmed BCL.
/// On Linux we run on the complete .NET 8 runtime, where those types would fail to load
/// ("Method 'add_PropertyChanged' ... does not have an implementation").
///
/// This pass re-adds every missing interface method implementation and abstract override:
///   * event accessors are rebuilt on top of the surviving compiler-generated backing field
///     (so events like INotifyPropertyChanged.PropertyChanged really work), and
///   * everything else gets a benign body (default return value / no-op).
/// </summary>
internal static class TrimRepair
{
    public static IReadOnlyList<string> RepairDirectory(string vendorDir, string frameworkDir)
    {
        var log = new List<string>();
        using var resolver = new DefaultAssemblyResolver();
        resolver.RemoveSearchDirectory(".");
        resolver.RemoveSearchDirectory("bin");
        // Framework first: vendor facades (e.g. System.Runtime.dll) must not shadow the real runtime.
        resolver.AddSearchDirectory(frameworkDir);
        resolver.AddSearchDirectory(vendorDir);

        foreach (string file in Directory.GetFiles(vendorDir, "*.dll").OrderBy(f => f, StringComparer.Ordinal))
        {
            string frameworkCopy = Path.Combine(frameworkDir, Path.GetFileName(file));
            if (File.Exists(frameworkCopy) || Path.GetFileName(file) == VendorManager.MainAssemblyFile)
            {
                continue; // runtime-provided facade, or the macOS app itself (only read for its version)
            }
            AssemblyDefinition assembly;
            try
            {
                assembly = AssemblyDefinition.ReadAssembly(new MemoryStream(File.ReadAllBytes(file)), new ReaderParameters { AssemblyResolver = resolver, InMemory = true });
            }
            catch (BadImageFormatException)
            {
                continue;
            }
            using (assembly)
            {
                if (assembly.MainModule.Resources.Any(r => r.Name == "VDStreamer.Linux.TrimRepaired"))
                {
                    continue;
                }
                int repaired = 0;
                foreach (TypeDefinition type in assembly.MainModule.GetTypes().ToList())
                {
                    if (type.IsInterface)
                    {
                        continue;
                    }
                    try
                    {
                        repaired += RepairInterfaces(type, log);
                        if (!type.IsAbstract)
                        {
                            repaired += RepairAbstractOverrides(type, log);
                        }
                    }
                    catch (AssemblyResolutionException ex)
                    {
                        log.Add($"  ! {type.FullName}: cannot resolve {ex.AssemblyReference.Name}");
                    }
                }
                if (repaired > 0)
                {
                    assembly.MainModule.Resources.Add(new EmbeddedResource("VDStreamer.Linux.TrimRepaired", ManifestResourceAttributes.Private, Array.Empty<byte>()));
                    string tmp = file + ".tmp";
                    assembly.Write(tmp);
                    File.Move(tmp, file, overwrite: true);
                    log.Add($"Repaired {repaired} trimmed member(s) in {Path.GetFileName(file)}");
                }
            }
        }
        return log;
    }

    // ---- interface implementations -------------------------------------------------------

    private static int RepairInterfaces(TypeDefinition type, List<string> log)
    {
        int count = 0;
        foreach (InterfaceImplementation impl in type.Interfaces.ToList())
        {
            foreach (TypeReference iface in ExpandInterfaces(impl.InterfaceType))
            {
                TypeDefinition ifaceDef = iface.Resolve();
                if (ifaceDef == null)
                {
                    continue;
                }
                foreach (MethodDefinition im in ifaceDef.Methods.Where(m => m.IsAbstract && !m.IsStatic))
                {
                    if (HasInterfaceImplementation(type, iface, im))
                    {
                        continue;
                    }
                    if (im.HasGenericParameters)
                    {
                        log.Add($"  ! {type.FullName}: generic {iface.FullName}.{im.Name} left unimplemented");
                        continue;
                    }
                    AddInterfaceImplementation(type, iface, im);
                    log.Add($"  + {type.FullName}: {iface.FullName}.{im.Name}");
                    count++;
                }
            }
        }
        return count;
    }

    private static IEnumerable<TypeReference> ExpandInterfaces(TypeReference iface)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<TypeReference>();
        stack.Push(iface);
        while (stack.Count > 0)
        {
            TypeReference current = stack.Pop();
            if (!seen.Add(current.FullName))
            {
                continue;
            }
            yield return current;
            TypeDefinition def = current.Resolve();
            if (def == null)
            {
                continue;
            }
            foreach (InterfaceImplementation parent in def.Interfaces)
            {
                stack.Push(Inflate(parent.InterfaceType, current));
            }
        }
    }

    private static bool HasInterfaceImplementation(TypeDefinition type, TypeReference iface, MethodDefinition im)
    {
        string[] wanted = Signature(im, iface);
        for (TypeDefinition t = type; t != null; t = t.BaseType?.Resolve())
        {
            foreach (MethodDefinition m in t.Methods)
            {
                if (m.Overrides.Any(o => o.Name == im.Name && SameType(o.DeclaringType, iface)))
                {
                    return true;
                }
                if (m.IsVirtual && m.IsPublic && m.Name == im.Name && m.Parameters.Count == im.Parameters.Count &&
                    m.GenericParameters.Count == im.GenericParameters.Count && Signature(m, null).SequenceEqual(wanted))
                {
                    return true;
                }
            }
        }
        // Default interface methods are fine; abstract ones are what we are here for.
        return false;
    }

    private static void AddInterfaceImplementation(TypeDefinition type, TypeReference iface, MethodDefinition im)
    {
        ModuleDefinition module = type.Module;
        // Open signature on the (possibly generic-instantiated) interface, as a MemberRef.
        var overridden = new MethodReference(im.Name, Import(module, im.ReturnType), Import(module, iface))
        {
            HasThis = im.HasThis,
            ExplicitThis = im.ExplicitThis,
            CallingConvention = im.CallingConvention,
        };
        foreach (ParameterDefinition p in im.Parameters)
        {
            overridden.Parameters.Add(new ParameterDefinition(Import(module, p.ParameterType)));
        }

        var method = new MethodDefinition(
            iface.FullName + "." + im.Name,
            MethodAttributes.Private | MethodAttributes.Final | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.HideBySig | (im.IsSpecialName ? MethodAttributes.SpecialName : 0),
            Import(module, Inflate(im.ReturnType, iface)));
        foreach (ParameterDefinition p in im.Parameters)
        {
            method.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, Import(module, Inflate(p.ParameterType, iface))));
        }
        method.Overrides.Add(overridden);
        EmitBody(type, method, im.Name);
        type.Methods.Add(method);
    }

    // ---- abstract base members -----------------------------------------------------------

    private static int RepairAbstractOverrides(TypeDefinition type, List<string> log)
    {
        int count = 0;
        var chain = new List<(TypeDefinition Def, TypeReference Ref)>();
        TypeReference baseRef = type.BaseType;
        while (baseRef != null)
        {
            TypeDefinition def = baseRef.Resolve();
            if (def == null)
            {
                break;
            }
            chain.Add((def, baseRef));
            baseRef = def.BaseType == null ? null : Inflate(def.BaseType, baseRef);
        }

        for (int i = 0; i < chain.Count; i++)
        {
            (TypeDefinition baseDef, TypeReference baseInst) = chain[i];
            foreach (MethodDefinition am in baseDef.Methods.Where(m => m.IsAbstract && m.IsVirtual && !m.IsStatic))
            {
                string[] wanted = Signature(am, baseInst);
                bool implemented = false;
                // Look in the type itself and every class between it and the declaring base.
                var candidates = new List<TypeDefinition> { type };
                candidates.AddRange(chain.Take(i).Select(c => c.Def));
                foreach (TypeDefinition c in candidates)
                {
                    if (c.Methods.Any(m => m.IsVirtual && !m.IsAbstract && m.Name == am.Name && m.Parameters.Count == am.Parameters.Count && Signature(m, null).SequenceEqual(wanted)) ||
                        c.Methods.Any(m => m.Overrides.Any(o => o.Name == am.Name && SameType(o.DeclaringType, baseInst))))
                    {
                        implemented = true;
                        break;
                    }
                }
                if (implemented)
                {
                    continue;
                }
                if (am.HasGenericParameters || type.Methods.Any(m => m.Name == am.Name && m.Parameters.Count == am.Parameters.Count && Signature(m, null).Skip(1).SequenceEqual(wanted.Skip(1))))
                {
                    log.Add($"  ! {type.FullName}: cannot add override for {baseDef.FullName}.{am.Name}");
                    continue;
                }
                ModuleDefinition module = type.Module;
                MethodAttributes visibility = am.Attributes & MethodAttributes.MemberAccessMask;
                if (visibility == MethodAttributes.FamORAssem && am.Module != module)
                {
                    visibility = MethodAttributes.Family;
                }
                var method = new MethodDefinition(am.Name, visibility | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.ReuseSlot | (am.IsSpecialName ? MethodAttributes.SpecialName : 0),
                    Import(module, Inflate(am.ReturnType, baseInst)));
                foreach (ParameterDefinition p in am.Parameters)
                {
                    method.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, Import(module, Inflate(p.ParameterType, baseInst))));
                }
                EmitBody(type, method, am.Name);
                type.Methods.Add(method);
                log.Add($"  + {type.FullName}: override {baseDef.FullName}.{am.Name}");
                count++;
            }
        }
        return count;
    }

    // ---- bodies ---------------------------------------------------------------------------

    private static void EmitBody(TypeDefinition type, MethodDefinition method, string memberName)
    {
        ModuleDefinition module = type.Module;
        ILProcessor il = method.Body.GetILProcessor();
        bool isAdd = memberName.StartsWith("add_", StringComparison.Ordinal);
        bool isRemove = memberName.StartsWith("remove_", StringComparison.Ordinal);
        if ((isAdd || isRemove) && method.Parameters.Count == 1)
        {
            string eventName = memberName.Substring(isAdd ? 4 : 7);
            FieldDefinition field = FindField(type, eventName, method.Parameters[0].ParameterType);
            if (field != null)
            {
                FieldReference fieldRef = module.ImportReference(field);
                if (type.HasGenericParameters && field.DeclaringType == type)
                {
                    fieldRef = new FieldReference(field.Name, field.FieldType, type.MakeGenericInstanceType(type.GenericParameters.ToArray()));
                }
                MethodReference op = module.ImportReference(typeof(Delegate).GetMethod(isAdd ? "Combine" : "Remove", new[] { typeof(Delegate), typeof(Delegate) }));
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, fieldRef);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Call, op);
                il.Emit(OpCodes.Castclass, method.Parameters[0].ParameterType);
                il.Emit(OpCodes.Stfld, fieldRef);
                il.Emit(OpCodes.Ret);
                return;
            }
        }

        TypeReference ret = method.ReturnType;
        if (ret.MetadataType == MetadataType.Void)
        {
            il.Emit(OpCodes.Ret);
            return;
        }
        if (ret.IsByReference)
        {
            // Cannot fabricate a reference; surface misuse clearly.
            il.Emit(OpCodes.Newobj, module.ImportReference(typeof(NotSupportedException).GetConstructor(Type.EmptyTypes)));
            il.Emit(OpCodes.Throw);
            return;
        }
        var local = new VariableDefinition(ret);
        method.Body.Variables.Add(local);
        method.Body.InitLocals = true;
        il.Emit(OpCodes.Ldloca_S, local);
        il.Emit(OpCodes.Initobj, ret);
        il.Emit(OpCodes.Ldloc, local);
        il.Emit(OpCodes.Ret);
    }

    private static FieldDefinition FindField(TypeDefinition type, string eventName, TypeReference handlerType)
    {
        for (TypeDefinition t = type; t != null; t = t.BaseType?.Resolve())
        {
            FieldDefinition f = t.Fields.FirstOrDefault(x => !x.IsStatic && (x.Name == eventName || x.Name == "<" + eventName + ">k__BackingField") && x.FieldType.FullName == handlerType.FullName);
            if (f != null)
            {
                return t == type ? f : (f.IsPrivate ? null : f);
            }
        }
        return null;
    }

    // ---- signature helpers -----------------------------------------------------------------

    private static string[] Signature(MethodDefinition m, TypeReference context)
    {
        var parts = new List<string> { Name(Inflate(m.ReturnType, context)) };
        parts.AddRange(m.Parameters.Select(p => Name(Inflate(p.ParameterType, context))));
        return parts.ToArray();
    }

    private static string Name(TypeReference t) => t.FullName;

    private static bool SameType(TypeReference a, TypeReference b) => a.FullName == b.FullName;

    /// <summary>Substitutes the generic arguments of <paramref name="context"/> into <paramref name="type"/>.</summary>
    private static TypeReference Inflate(TypeReference type, TypeReference context)
    {
        if (context is not GenericInstanceType git)
        {
            return type;
        }
        switch (type)
        {
            case GenericParameter gp when gp.Type == GenericParameterType.Type && gp.Position < git.GenericArguments.Count:
                return git.GenericArguments[gp.Position];
            case ByReferenceType br:
                return new ByReferenceType(Inflate(br.ElementType, context));
            case ArrayType at:
                return new ArrayType(Inflate(at.ElementType, context), at.Rank);
            case PointerType pt:
                return new PointerType(Inflate(pt.ElementType, context));
            case GenericInstanceType inner:
                var result = new GenericInstanceType(inner.ElementType);
                foreach (TypeReference arg in inner.GenericArguments)
                {
                    result.GenericArguments.Add(Inflate(arg, context));
                }
                return result;
            default:
                return type;
        }
    }

    /// <summary>
    /// Imports a (possibly composite) type into <paramref name="module"/>. Generic parameters are
    /// kept as-is: they are owned by vendor types/methods or are positional (!0) in MemberRef
    /// signatures, neither of which needs importing.
    /// </summary>
    private static TypeReference Import(ModuleDefinition module, TypeReference type)
    {
        switch (type)
        {
            case GenericParameter:
                return type;
            case ByReferenceType br:
                return new ByReferenceType(Import(module, br.ElementType));
            case ArrayType at:
                return new ArrayType(Import(module, at.ElementType), at.Rank);
            case PointerType pt:
                return new PointerType(Import(module, pt.ElementType));
            case GenericInstanceType git:
                var result = new GenericInstanceType(Import(module, git.ElementType));
                foreach (TypeReference arg in git.GenericArguments)
                {
                    result.GenericArguments.Add(Import(module, arg));
                }
                return result;
            default:
                return type.Module == module ? type : module.ImportReference(type);
        }
    }
}
