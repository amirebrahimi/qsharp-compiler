﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.Quantum.QsCompiler.CompilationBuilder;
using Microsoft.Quantum.QsCompiler.ReservedKeywords;
using Microsoft.Quantum.QsCompiler.Serialization;
using Microsoft.Quantum.QsCompiler.SyntaxTree;
using Microsoft.Quantum.QsCompiler.Transformations;
using Newtonsoft.Json.Bson;

namespace Microsoft.Quantum.QsCompiler
{
    /// <summary>
    /// This class relies on the ECMA-335 standard to extract information contained in compiled binaries.
    /// The standard can be found here: https://www.ecma-international.org/publications/files/ECMA-ST/ECMA-335.pdf,
    /// and the section on custom attributes starts on page 267.
    /// </summary>
    public static class AssemblyLoader
    {
        /// <summary>
        /// Loads the Q# data structures in a referenced assembly given the Uri to that assembly,
        /// and returns the loaded content as out parameter.
        /// Returns false if some of the content could not be loaded successfully,
        /// possibly because the referenced assembly has been compiled with an older compiler version.
        /// If onDeserializationException is specified, invokes the given action on any exception thrown during deserialization.
        /// Throws an ArgumentException if the URI is not an absolute file URI.
        /// Throws a FileNotFoundException if no file with the given name exists.
        /// Throws the corresponding exceptions if the information cannot be extracted.
        /// </summary>
        public static bool LoadReferencedAssembly(Uri asm, out References.Headers headers, bool ignoreDllResources = false, Action<Exception>? onDeserializationException = null)
        {
            var id = CompilationUnitManager.GetFileId(asm);
            if (!File.Exists(asm.LocalPath))
            {
                throw new FileNotFoundException($"The file '{asm.LocalPath}' given to the assembly loader does not exist.");
            }

            using var stream = File.OpenRead(asm.LocalPath);
            using var assemblyFile = new PEReader(stream);
            if (ignoreDllResources || !FromResource(assemblyFile, out var compilation, onDeserializationException))
            {
                var attributes = LoadHeaderAttributes(assemblyFile);
                headers = new References.Headers(id, attributes);
                return ignoreDllResources || !attributes.Any(); // just means we have no references
            }
            headers = new References.Headers(id, compilation?.Namespaces ?? ImmutableArray<QsNamespace>.Empty);
            return true;
        }

        /// <summary>
        /// Loads the Q# data structures in a referenced assembly given the Uri to that assembly,
        /// and returns the loaded content as out parameter.
        /// Returns false if some of the content could not be loaded successfully,
        /// possibly because the referenced assembly has been compiled with an older compiler version.
        /// Catches any exception throw upon loading the compilation, and invokes onException with it if such an action has been specified.
        /// Sets the out parameter to null if an exception occurred during loading.
        /// Throws a FileNotFoundException if no file with the given name exists.
        /// </summary>
        public static bool LoadReferencedAssembly(
            string asmPath,
            [NotNullWhen(true)] out QsCompilation? compilation,
            Action<Exception>? onException = null)
        {
            if (!File.Exists(asmPath))
            {
                throw new FileNotFoundException($"The file '{asmPath}' does not exist.");
            }

            using var stream = File.OpenRead(asmPath);
            using var assemblyFile = new PEReader(stream);
            try
            {
                return FromResource(assemblyFile, out compilation, onException);
            }
            catch (Exception ex)
            {
                onException?.Invoke(ex);
                compilation = null;
                return false;
            }
        }

        // tools for loading the compiled syntax tree from the dll resource (later setup for shipping Q# libraries)

        /// <summary>
        /// Given a stream containing the binary representation of compiled Q# code, returns the corresponding Q# compilation.
        /// Returns true if the compilation could be deserialized without throwing an exception, and false otherwise.
        /// If onDeserializationException is specified, invokes the given action on any exception thrown during deserialization.
        /// </summary>
        public static bool LoadSyntaxTree(
            Stream stream,
            [NotNullWhen(true)] out QsCompilation? compilation,
            Action<Exception>? onDeserializationException = null)
        {
            using var reader = new BsonDataReader(stream);
            (compilation, reader.ReadRootValueAsArray) = (null, false);
            try
            {
                compilation = Json.Serializer.Deserialize<QsCompilation>(reader);
                return compilation != null && !compilation.Namespaces.IsDefault && !compilation.EntryPoints.IsDefault;
            }
            catch (Exception ex)
            {
                onDeserializationException?.Invoke(ex);
                return false;
            }
        }

        /// <summary>
        /// Creates a dictionary of all manifest resources in the given reader.
        /// </summary>
        private static ImmutableDictionary<string, ManifestResource> Resources(this MetadataReader reader) =>
            reader.ManifestResources
                .Select(reader.GetManifestResource)
                .ToImmutableDictionary(
                    resource => reader.GetString(resource.Name),
                    resource => resource);

        /// <summary>
        /// Given a reader for the byte stream of a dotnet dll, loads any Q# compilation included as a resource.
        /// Returns true as well as the loaded compilation if the given dll includes a suitable resource, and returns false otherwise.
        /// If onDeserializationException is specified, invokes the given action on any exception thrown during deserialization.
        /// May throw an exception if the given binary file has been compiled with a different compiler version.
        /// </summary>
        private static bool FromResource(
            PEReader assemblyFile,
            [NotNullWhen(true)] out QsCompilation? compilation,
            Action<Exception>? onDeserializationException = null)
        {
            var metadataReader = assemblyFile.GetMetadataReader();
            compilation = null;

            // The offset of resources is relative to the resources directory.
            // It is possible that there is no offset given because a valid dll allows for extenal resources.
            // In all Q# dlls there will be a resource with the specific name chosen by the compiler.
            var resourceDir = assemblyFile.PEHeaders.CorHeader.ResourcesDirectory;
            if (!assemblyFile.PEHeaders.TryGetDirectoryOffset(resourceDir, out var directoryOffset) ||
                !metadataReader.Resources().TryGetValue(DotnetCoreDll.ResourceName, out var resource) ||
                !resource.Implementation.IsNil)
            {
                return false;
            }

            // This is going to be very slow, as it loads the entire assembly into a managed array, byte by byte.
            // Due to the finite size of the managed array, that imposes a memory limitation of around 4GB.
            // The other alternative would be to have an unsafe block, or to contribute a fix to PEMemoryBlock to expose a ReadOnlySpan.
            var image = assemblyFile.GetEntireImage(); // uses int to denote the length and access parameters
            var absResourceOffset = (int)resource.Offset + directoryOffset;

            // the first four bytes of the resource denote how long the resource is, and are followed by the actual resource data
            var resourceLength = BitConverter.ToInt32(image.GetContent(absResourceOffset, sizeof(int)).ToArray(), 0);
            var resourceData = image.GetContent(absResourceOffset + sizeof(int), resourceLength).ToArray();
            return LoadSyntaxTree(new MemoryStream(resourceData), out compilation, onDeserializationException);
        }

        // tools for loading headers based on attributes in compiled C# code (early setup for shipping Q# libraries)

        /// <summary>
        /// There are two possible handle kinds in use for the constructor of a custom attribute,
        /// one pointing to the MethodDef table and one to the MemberRef table, see p.216 in the ECMA standard linked above and
        /// https://github.com/dotnet/corefx/blob/master/src/System.Reflection.Metadata/src/System/Reflection/Metadata/TypeSystem/CustomAttribute.cs#L42
        /// This routine extracts the namespace and type name of the given attribute and returns the corresponding string handles.
        /// Returns null if the constructor handle is not a MethodDefinition or a MemberDefinition.
        /// </summary>
        private static (StringHandle, StringHandle)? GetAttributeType(MetadataReader metadataReader, CustomAttribute attribute)
        {
            if (attribute.Constructor.Kind == HandleKind.MethodDefinition)
            {
                var ctor = metadataReader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
                var type = metadataReader.GetTypeDefinition(ctor.GetDeclaringType());
                return (type.Namespace, type.Name);
            }
            else if (attribute.Constructor.Kind == HandleKind.MemberReference)
            {
                var ctor = metadataReader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                var type = metadataReader.GetTypeReference((TypeReferenceHandle)ctor.Parent);
                return (type.Namespace, type.Name);
            }
            else
            {
                return null;
            }
        }

        // TODO: this needs to be made more robust.
        // We currently rely on the fact that all attributes defined by the Q# compiler
        // have a single constructor taking a single string argument.
        private static (string, string)? GetAttribute(MetadataReader metadataReader, CustomAttribute attribute)
        {
            var attrType = GetAttributeType(metadataReader, attribute);
            QsCompilerError.Verify(attrType.HasValue, "the type of the custom attribute could not be determined");
            var (ns, name) = attrType!.Value;

            var attrNS = metadataReader.GetString(ns);
            if (attrNS.StartsWith("Microsoft.Quantum", StringComparison.InvariantCulture))
            {
                var attrReader = metadataReader.GetBlobReader(attribute.Value);
                _ = attrReader.ReadUInt16(); // All custom attributes start with 0x0001, so read that now and discard it.
                try
                {
                    var serialization = attrReader.ReadSerializedString(); // FIXME: this needs to be made more robust
                    return (metadataReader.GetString(name), serialization);
                }
                catch
                {
                    return null;
                }
            }
            return null;
        }

        /// <summary>
        /// Given a reader for the byte stream of a dotnet dll, read its custom attributes and
        /// returns a tuple containing the name of the attribute and the constructor argument
        /// for all attributes defined in a Microsoft.Quantum* namespace.
        /// Throws the corresponding exceptions if the information cannot be extracted.
        /// </summary>
        private static IEnumerable<(string, string)> LoadHeaderAttributes(PEReader assemblyFile)
        {
            var metadataReader = assemblyFile.GetMetadataReader();
            return metadataReader.GetAssemblyDefinition().GetCustomAttributes()
                .Select(metadataReader.GetCustomAttribute)
                .SelectNotNull(attribute => GetAttribute(metadataReader, attribute))
                .ToImmutableArray();
        }
    }
}
