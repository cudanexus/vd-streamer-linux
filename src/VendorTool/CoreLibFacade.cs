using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

namespace VDStreamer;

/// <summary>
/// The vendor assemblies were trimmed by the .NET-for-macOS toolchain, which rewrote their
/// type references to point at System.Private.CoreLib. C# cannot compile against that, so we
/// generate a compile-only "System.Private.CoreLib" facade that forwards each referenced type to
/// the public reference assembly declaring it. At runtime the real CoreLib is used.
/// </summary>
internal static class CoreLibFacade
{
    public static void Generate(string vendorDir, string refPackDir, string outputFile)
    {
        var owners = new Dictionary<string, AssemblyNameReference>(StringComparer.Ordinal);
        foreach (string refAssembly in Directory.GetFiles(refPackDir, "*.dll"))
        {
            using AssemblyDefinition asm = AssemblyDefinition.ReadAssembly(refAssembly);
            var name = AssemblyNameReference.Parse(asm.FullName);
            foreach (TypeDefinition type in asm.MainModule.Types.Where(t => t.IsPublic))
            {
                owners.TryAdd(type.FullName, name);
            }
            foreach (ExportedType exported in asm.MainModule.ExportedTypes.Where(t => t.DeclaringType == null))
            {
                // Forwarders inside the ref pack (e.g. netstandard.dll) are not definitions; skip.
            }
        }

        var needed = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.GetFiles(vendorDir, "*.dll"))
        {
            AssemblyDefinition asm;
            try
            {
                asm = AssemblyDefinition.ReadAssembly(file);
            }
            catch (BadImageFormatException)
            {
                continue;
            }
            using (asm)
            {
                foreach (TypeReference tr in asm.MainModule.GetTypeReferences())
                {
                    TypeReference top = tr;
                    while (top.DeclaringType != null)
                    {
                        top = top.DeclaringType;
                    }
                    if (top.Scope is AssemblyNameReference scope && scope.Name == "System.Private.CoreLib")
                    {
                        needed.Add(top.FullName);
                    }
                }
            }
        }

        // References carry CoreLib's public key token, so the facade must expose the same public
        // key (copied from the running runtime's CoreLib). The compiler does not verify signatures.
        byte[] publicKey;
        using (AssemblyDefinition coreLib = AssemblyDefinition.ReadAssembly(typeof(object).Assembly.Location))
        {
            publicKey = coreLib.Name.PublicKey;
        }
        var facadeName = new AssemblyNameDefinition("System.Private.CoreLib", new Version(8, 0, 0, 0))
        {
            PublicKey = publicKey,
            HashAlgorithm = AssemblyHashAlgorithm.SHA1,
        };
        var facade = AssemblyDefinition.CreateAssembly(facadeName, "System.Private.CoreLib.dll", ModuleKind.Dll);
        ModuleDefinition module = facade.MainModule;
        var refs = new Dictionary<string, AssemblyNameReference>(StringComparer.Ordinal);
        int forwarded = 0;
        var unresolved = new List<string>();
        foreach (string typeName in needed)
        {
            if (!owners.TryGetValue(typeName, out AssemblyNameReference owner))
            {
                unresolved.Add(typeName);
                continue;
            }
            if (!refs.TryGetValue(owner.Name, out AssemblyNameReference local))
            {
                local = new AssemblyNameReference(owner.Name, owner.Version) { PublicKeyToken = owner.PublicKeyToken };
                module.AssemblyReferences.Add(local);
                refs[owner.Name] = local;
            }
            int dot = typeName.LastIndexOf('.');
            string ns = dot < 0 ? string.Empty : typeName.Substring(0, dot);
            string name = dot < 0 ? typeName : typeName.Substring(dot + 1);
            module.ExportedTypes.Add(new ExportedType(ns, name, module, local) { Attributes = TypeAttributes.Forwarder });
            forwarded++;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputFile)));
        facade.Write(outputFile);
        Console.WriteLine($"Generated compile facade with {forwarded} forwarders ({unresolved.Count} CoreLib-internal types left unresolved)");
    }
}
