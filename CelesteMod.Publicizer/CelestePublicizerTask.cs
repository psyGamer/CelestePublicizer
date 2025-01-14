using System;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AsmResolver;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using BepInEx.AssemblyPublicizer;
using FieldAttributes = AsmResolver.PE.DotNet.Metadata.Tables.FieldAttributes;
using MethodAttributes = AsmResolver.PE.DotNet.Metadata.Tables.MethodAttributes;
using TypeAttributes = AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes;

namespace CelesteMod.Publicizer;

public class PublicizeCelesteTask : Task {

    [Required]
    public string IntermediateOutputPath { get; set; } = null!;
    
    [Required]
    public ITaskItem[] PackageReference { get; set; } = null!;
    
    [Output]
    public ITaskItem PublicizedReference { get; private set; } = null!;

    public override bool Execute() {
        const string PackageName = "CelesteMod.Publicizer";
        var celestePackages = PackageReference
            .Where(item => item.TryGetMetadata("Identity", out var identity) && identity == PackageName)
            .ToArray();

        if (celestePackages.Length == 0) return true;
        if (celestePackages.Length > 1) {
            Log.LogError($"Specified {PackageName} package more than once");
            return false;
        }
        
        var celestePackage = celestePackages[0];
        if (!celestePackage.TryGetMetadata("CelesteAssembly", out var celesteAssemblyPath)) {
            Log.LogError($"The \"CelesteAssembly\" property needs to be specified for the {PackageName} package");
            return false;
        }
        
        // Clear old leftover DLLs
        if (File.Exists($"{IntermediateOutputPath}Celeste-publicized.dll"))
            File.Delete($"{IntermediateOutputPath}Celeste-publicized.dll");
        if (File.Exists($"{IntermediateOutputPath}Celeste-publicized.dll.md5"))
            File.Delete($"{IntermediateOutputPath}Celeste-publicized.dll.md5");
        if (File.Exists($"{IntermediateOutputPath}Celeste-publicized.xml"))
            File.Delete($"{IntermediateOutputPath}Celeste-publicized.xml");
        
        string outputAssemblyPath = Path.Combine(Path.GetTempPath(), "Celeste-publicized.dll");
        string outputHashPath = Path.ChangeExtension(outputAssemblyPath, "md5");
        
        var taskAssembly = typeof(PublicizeCelesteTask).Assembly;
        
        var celesteAssemblyBytes = File.ReadAllBytes(celesteAssemblyPath);
        var celesteAssembly = AssemblyDefinition.FromBytes(celesteAssemblyBytes);
        
        var origAssemblyStream = taskAssembly.GetManifestResourceStream($"{PackageName}.Assets.Celeste.exe")!;
        var memoryStream = new MemoryStream(); 
        origAssemblyStream.CopyTo(memoryStream);
        var origAssemblyBytes = memoryStream.ToArray();
        var origAssembly = AssemblyDefinition.FromBytes(origAssemblyBytes);

        var hash = ComputeHash(celesteAssemblyBytes, origAssemblyBytes);
        if (File.Exists(outputHashPath) && File.ReadAllText(outputHashPath) == hash) 
        {
            Log.LogMessage($"{celesteAssemblyPath} was already publicized, skipping");
        } 
        else
        {
            Log.LogMessage($"Publicizing {celesteAssemblyPath}...");
            
            PublicizeAssembly(celesteAssembly, origAssembly);
        
            var module = celesteAssembly.ManifestModule;
            module!.FatalWrite(outputAssemblyPath);
            
            var originalDocumentationPath = Path.ChangeExtension(celesteAssemblyPath, "xml");
            if (File.Exists(originalDocumentationPath)) {
                File.Copy(originalDocumentationPath, Path.ChangeExtension(outputAssemblyPath, "xml"), true);
            }
        
            File.WriteAllText(outputHashPath, hash);
            Log.LogMessage($"Publicized {celesteAssemblyPath}");
        }

        PublicizedReference = new TaskItem(outputAssemblyPath);
        celestePackage.CopyMetadataTo(PublicizedReference);
        celestePackage.RemoveMetadata("ReferenceAssembly");
        
        return true;
    }
    
    // class name -> member name -> reason
    private static readonly Dictionary<string, Dictionary<string, string>> blacklist = new() {
        // TODO: There is still discussion about this, so it's not yet published
        // {"Celeste.Player", new() {
        //     {"onGround", "Consider using OnGround instead"}
        // }},
        {"Monocle.Entity", new() {
            {"tag", "Using 'tag' directly can cause unexpected issues. Consider using 'Tag' instead"},
            {"collider", "Using 'collider' directly can cause unexpected issues. Consider using 'Collider' instead"},
            {"depth", "Using 'depth' directly can cause unexpected issues. Consider using 'Depth' instead"},
        }}
    };
    
    // Adapted from https://github.com/psyGamer/BepInEx.AssemblyPublicizer/blob/master/BepInEx.AssemblyPublicizer/AssemblyPublicizer.cs
    private static void PublicizeAssembly(AssemblyDefinition assembly, AssemblyDefinition maskAssembly)
    {
        var module = assembly.ManifestModule!;
        var maskModule = maskAssembly.ManifestModule!;

        var maskTypes = maskModule.GetAllTypes().ToDictionary(x => x.FullName);
        
        var attribute = new OrigVisibilityAttribute(module);
        
        foreach (var typeDefinition in module.GetAllTypes()) {
            if (maskTypes.TryGetValue(typeDefinition.FullName, out var type))
                PublicizeType(typeDefinition, type, attribute);
        }
    }
    
    private static void PublicizeType(TypeDefinition typeDefinition, TypeDefinition maskTypeDefinition, OrigVisibilityAttribute attribute) {
        var typeBlacklist = blacklist.TryGetValue(typeDefinition.FullName, out var x) ? x : null;
        
        if (typeDefinition is { IsNested: false, IsPublic: false } or { IsNested: true, IsNestedPublic: false }) {
            var origAttrs = typeDefinition.Attributes;
            
            typeDefinition.Attributes &= ~TypeAttributes.VisibilityMask;
            typeDefinition.Attributes |= typeDefinition.IsNested ? TypeAttributes.NestedPublic : TypeAttributes.Public;
            
            typeDefinition.CustomAttributes.Add(attribute.ToCustomAttribute(origAttrs & TypeAttributes.VisibilityMask));
        }

        var maskMethods = maskTypeDefinition.Methods.Select(def => def.FullName).ToArray();
        foreach (var methodDefinition in typeDefinition.Methods) {
            if (maskMethods != null && !maskMethods.Contains(methodDefinition.FullName))
                continue;
            PublicizeMethod(methodDefinition, attribute);
        }

        foreach (var propertyDefinition in typeDefinition.Properties)
        {
            if (propertyDefinition.GetMethod is { } getMethod) PublicizeMethod(getMethod, attribute, ignoreCompilerGeneratedCheck: true);
            if (propertyDefinition.SetMethod is { } setMethod) PublicizeMethod(setMethod, attribute, ignoreCompilerGeneratedCheck: true);
        }

        var maskFields = maskTypeDefinition.Fields.Select(def => def.FullName).ToArray();
        
        var eventNames = new HashSet<Utf8String?>(typeDefinition.Events.Select(e => e.Name));
        foreach (var fieldDefinition in typeDefinition.Fields)
        {
            if (fieldDefinition.IsPrivateScope)
                continue;
            if (maskFields != null && !maskFields.Contains(fieldDefinition.FullName))
                continue;

            if (!fieldDefinition.IsPublic)
            {
                // Skip event backing fields
                if (eventNames.Contains(fieldDefinition.Name))
                    continue;
                if (fieldDefinition.IsCompilerGenerated())
                    continue;
                
                var origAttrs = fieldDefinition.Attributes;

                fieldDefinition.Attributes &= ~FieldAttributes.FieldAccessMask;
                fieldDefinition.Attributes |= FieldAttributes.Public;

                fieldDefinition.CustomAttributes.Add(attribute.ToCustomAttribute(origAttrs & FieldAttributes.FieldAccessMask));
                if (typeBlacklist?.TryGetValue(fieldDefinition.Name!, out var reason) ?? false)
                    fieldDefinition.CustomAttributes.AddObsoleteAttribute(fieldDefinition.Module!, reason, error: false);
            }
        }
    }
    
    private static void PublicizeMethod(MethodDefinition methodDefinition, OrigVisibilityAttribute attribute, bool ignoreCompilerGeneratedCheck = false) {
        if (methodDefinition.IsCompilerControlled)
            return;
        if (!ignoreCompilerGeneratedCheck && methodDefinition.IsCompilerGenerated())
            return;
        if (methodDefinition.IsPublic) 
            return;
        
        var typeBlacklist = blacklist.TryGetValue(methodDefinition.DeclaringType!.FullName, out var x) ? x : null;

        var origAttrs = methodDefinition.Attributes;
            
        methodDefinition.Attributes &= ~MethodAttributes.MemberAccessMask;
        methodDefinition.Attributes |= MethodAttributes.Public;
            
        methodDefinition.CustomAttributes.Add(attribute.ToCustomAttribute(origAttrs & MethodAttributes.MemberAccessMask));
        if (typeBlacklist?.TryGetValue(methodDefinition.Name!, out var reason) ?? false)
            methodDefinition.CustomAttributes.AddObsoleteAttribute(methodDefinition.Module!, reason, error: false);
    }
    
    // Adapted from https://github.com/BepInEx/BepInEx.AssemblyPublicizer/blob/master/BepInEx.AssemblyPublicizer.MSBuild/PublicizeTask.cs#L132-L168
    private static string ComputeHash(byte[] bytes, byte[] maskBytes) {
        static void Hash(ICryptoTransform hash, byte[] buffer) {
            hash.TransformBlock(buffer, 0, buffer.Length, buffer, 0);
        }

        static void HashString(ICryptoTransform hash, string str) => Hash(hash, Encoding.UTF8.GetBytes(str));

        using var md5 = MD5.Create();

        HashString(md5, typeof(PublicizeCelesteTask).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);
        Hash(md5, maskBytes);

        md5.TransformFinalBlock(bytes, 0, bytes.Length);

        return ByteArrayToString(md5.Hash);
    }
    
    private static string ByteArrayToString(IReadOnlyCollection<byte> data) {
        var builder = new StringBuilder(data.Count * 2);
        foreach (var b in data) {
            builder.Append($"{b:x2}");
        }
        return builder.ToString();
    }
}

// Taken from https://github.com/BepInEx/BepInEx.AssemblyPublicizer/blob/master/BepInEx.AssemblyPublicizer.MSBuild/Extensions.cs
internal static class Extensions {
    public static bool HasMetadata(this ITaskItem taskItem, string metadataName) {
        var metadataNames = (ICollection<string>)taskItem.MetadataNames;
        return metadataNames.Contains(metadataName);
    }

    public static bool TryGetMetadata(this ITaskItem taskItem, string metadataName, [NotNullWhen(true)] out string? metadata) {
        if (taskItem.HasMetadata(metadataName)) {
            metadata = taskItem.GetMetadata(metadataName);
            return true;
        }

        metadata = null;
        return false;
    }
    
    public static void AddObsoleteAttribute(this IList<CustomAttribute> attributes, ModuleDefinition module, string reason, bool error)
    {
        var corLibTypeFactory = module.CorLibTypeFactory; 
        var importer = module.DefaultImporter;
        attributes.Add(new CustomAttribute(
            importer.ImportType(typeof(ObsoleteAttribute)).CreateMemberReference(".ctor", MethodSignature.CreateInstance(corLibTypeFactory.Void, corLibTypeFactory.String, corLibTypeFactory.Boolean)).ImportWith(importer),
            new CustomAttributeSignature([
                new CustomAttributeArgument(corLibTypeFactory.String, reason),
                new CustomAttributeArgument(corLibTypeFactory.Boolean, error),
            ]))
        );
    }
}
