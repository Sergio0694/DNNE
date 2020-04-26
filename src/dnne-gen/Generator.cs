﻿// Copyright 2020 Aaron R Robinson
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is furnished
// to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A
// PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
// HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
// SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace DNNE
{
    class GeneratorException : Exception
    {
        public string AssemblyPath { get; private set; }

        public GeneratorException(string assemblyPath, string message)
            : base(message)
        {
            this.AssemblyPath = assemblyPath;
        }
    }

    class Generator : IDisposable
    {
        private bool isDisposed = false;

        private readonly ICustomAttributeTypeProvider<KnownType> typeResolver = new TypeResolver();
        private readonly ISignatureTypeProvider<string, UnusedGenericContext> typeProvider = new C99TypeProvider();
        private readonly string assemblyPath;
        private readonly PEReader peReader;
        private readonly MetadataReader mdReader;

        public Generator(string validAssemblyPath)
        {
            this.assemblyPath = validAssemblyPath;
            this.peReader = new PEReader(File.OpenRead(this.assemblyPath));
            this.mdReader = this.peReader.GetMetadataReader(MetadataReaderOptions.None);
        }

        public void Emit(string outputFile)
        {
            var generatedCode = new StringWriter();
            Emit(generatedCode);

            // Check if the file exists
            if (File.Exists(outputFile))
            {
                File.Delete(outputFile);
            }

            // Write the generated code to the output file.
            using (var outputFileStream = new StreamWriter(File.OpenWrite(outputFile)))
            {
                outputFileStream.Write(generatedCode.ToString());
            }
        }

        public void Emit(TextWriter outputStream)
        {
            var exportedMethods = new List<ExportedMethod>();
            foreach (var methodDefHandle in this.mdReader.MethodDefinitions)
            {
                MethodDefinition methodDef = this.mdReader.GetMethodDefinition(methodDefHandle);

                // Only check public static functions
                if (!methodDef.Attributes.HasFlag(MethodAttributes.Public | MethodAttributes.Static))
                {
                    continue;
                }

                bool foundTargetAttr = false;
                string managedMethodName = this.mdReader.GetString(methodDef.Name);
                string exportName = managedMethodName;
                // Check for target attribute
                foreach (var customAttrHandle in methodDef.GetCustomAttributes())
                {
                    CustomAttribute customAttr = this.mdReader.GetCustomAttribute(customAttrHandle);
                    foundTargetAttr = this.IsTargetAttribute(customAttr);
                    if (!foundTargetAttr)
                    {
                        continue;
                    }

                    CustomAttributeValue<KnownType> data = customAttr.DecodeValue(this.typeResolver);
                    if (data.NamedArguments.Length == 1)
                    {
                        exportName = (string)data.NamedArguments[0].Value;
                    }
                    break;
                }

                // Didn't find target attribute. Move onto next method.
                if (!foundTargetAttr)
                {
                    continue;
                }

                // Extract method details
                var typeDef = this.mdReader.GetTypeDefinition(methodDef.GetDeclaringType());
                var classString = this.mdReader.GetString(typeDef.Name);

                // Exporting from nested types is not supported.
                if (typeDef.IsNested)
                {
                    throw new GeneratorException(this.assemblyPath, $"Method '{this.mdReader.GetString(methodDef.Name)}' is being exported by nested type {classString}.");
                }

                var namespaceString = this.mdReader.GetString(typeDef.Namespace);
                var method = new ExportedMethod()
                {
                    EnclosingTypeName = namespaceString + Type.Delimiter + classString,
                    MethodName = managedMethodName,
                    ExportName = exportName,
                };

                // Process method signature.
                MethodSignature<string> signature;
                try
                {
                    signature = methodDef.DecodeSignature(typeProvider, null);
                }
                catch (NotSupportedTypeException nste)
                {
                    throw new GeneratorException(this.assemblyPath, $"Method '{this.mdReader.GetString(methodDef.Name)}' has non-exportable type '{nste.Type}'");
                }

                // Add method types.
                method.ArgumentTypes.AddRange(signature.ParameterTypes);

                // Process each method argument name.
                foreach (ParameterHandle paramHandle in methodDef.GetParameters())
                {
                    Parameter param = this.mdReader.GetParameter(paramHandle);
                    method.ArgumentNames.Add(this.mdReader.GetString(param.Name));
                }

                // Set the return type
                method.ReturnType = signature.ReturnType;

                exportedMethods.Add(method);
            }

            if (exportedMethods.Count == 0)
            {
                throw new GeneratorException(this.assemblyPath, "Nothing to export.");
            }

            string assemblyName = this.mdReader.GetString(this.mdReader.GetAssemblyDefinition().Name);
            EmitC99(outputStream, assemblyName, exportedMethods);
        }

        public void Dispose()
        {
            if (this.isDisposed)
            {
                return;
            }

            this.peReader.Dispose();

            this.isDisposed = true;
        }

        private bool IsTargetAttribute(CustomAttribute attribute)
        {
            return IsAttributeType(this.mdReader, attribute, "DNNE", "ExportAttribute");
        }

        private static bool IsAttributeType(MetadataReader reader, CustomAttribute attribute, string targetNamespace, string targetName)
        {
            StringHandle namespaceMaybe;
            StringHandle nameMaybe;
            switch (attribute.Constructor.Kind)
            {
                case HandleKind.MemberReference:
                    MemberReference refConstructor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                    TypeReference refType = reader.GetTypeReference((TypeReferenceHandle)refConstructor.Parent);
                    namespaceMaybe = refType.Namespace;
                    nameMaybe = refType.Name;
                    break;

                case HandleKind.MethodDefinition:
                    MethodDefinition defConstructor = reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
                    TypeDefinition defType = reader.GetTypeDefinition(defConstructor.GetDeclaringType());
                    namespaceMaybe = defType.Namespace;
                    nameMaybe = defType.Name;
                    break;

                default:
                    Debug.Assert(false, "Unknown attribute constructor kind");
                    return false;
            }

            return reader.StringComparer.Equals(namespaceMaybe, targetNamespace) && reader.StringComparer.Equals(nameMaybe, targetName);
        }

        private static void EmitC99(TextWriter outputStream, string assemblyName, IEnumerable<ExportedMethod> exports)
        {
            // Emit header/preamble
            outputStream.WriteLine(
@"//
// Auto-generated by dnne-gen
//

#include <stdint.h>
#include <dnne.h>

//
// Forward declarations
//

extern void* get_callable_managed_function(
    const char_t* dotnet_type,
    const char_t* dotnet_type_method,
    const char_t* dotnet_delegate_type);");

            // Emit string table
            outputStream.WriteLine(
@"
//
// string constants
//
");
            int count = 1;
            var map = new StringDictionary();
            foreach (var method in exports)
            {
                if (map.ContainsKey(method.EnclosingTypeName))
                {
                    continue;
                }

                string id = $"t{count++}_name";
                outputStream.WriteLine($"static const char_t* {id} = NE_STR(\"{method.EnclosingTypeName}, {assemblyName}\");");
                map.Add(method.EnclosingTypeName, id);
            }

            // Emit the exports
            outputStream.WriteLine(
@"
//
// exports
//
");
            foreach (var export in exports)
            {
                // Create declaration and call signature.
                string delim = "";
                var declsig = new StringBuilder();
                var callsig = new StringBuilder();
                for (int i = 0; i < export.ArgumentTypes.Count; ++i)
                {
                    if (i > 0)
                    {
                        delim = ", ";
                    }
                    declsig.AppendFormat("{0}{1} {2}", delim, export.ArgumentTypes[i], export.ArgumentNames[i]);
                    callsig.AppendFormat("{0}{1}", delim, export.ArgumentNames[i]);
                }

                // Special casing for void signature.
                if (declsig.Length == 0)
                {
                    declsig.Append("void");
                }

                // Special casing for void return.
                string returnStatementKeyword = "return ";
                if (export.ReturnType.Equals("void"))
                {
                    returnStatementKeyword = string.Empty;
                }

                string classNameConstant = map[export.EnclosingTypeName];
                Debug.Assert(!string.IsNullOrEmpty(classNameConstant));

                outputStream.WriteLine(
@$"// Computed from {export.EnclosingTypeName}{Type.Delimiter}{export.MethodName}
static {export.ReturnType} (NE_CALLTYPE* {export.ExportName}_ptr)({declsig});
NE_API {export.ReturnType} NE_CALLTYPE {export.ExportName}({declsig})
{{
    if ({export.ExportName}_ptr == NULL)
    {{
        const char_t* methodName = NE_STR(""{export.MethodName}"");
        const char_t* delegateType = NE_STR(""{export.EnclosingTypeName}+{export.MethodName}Delegate, {assemblyName}"");
        {export.ExportName}_ptr = get_callable_managed_function({classNameConstant}, methodName, delegateType);
    }}
    {returnStatementKeyword}{export.ExportName}_ptr({callsig});
}}
");
            }
        }

        private class ExportedMethod
        {
            public string EnclosingTypeName { get; set; }
            public string MethodName { get; set; }
            public string ExportName { get; set; }
            public string ReturnType { get; set; }
            public List<string> ArgumentTypes { get; } = new List<string>();
            public List<string> ArgumentNames { get; } = new List<string>();
        }

        private enum KnownType
        {
            String,
            SystemType,
            Unknown
        }

        private class TypeResolver : ICustomAttributeTypeProvider<KnownType>
        {
            public KnownType GetPrimitiveType(PrimitiveTypeCode typeCode)
            {
                return typeCode switch
                {
                    PrimitiveTypeCode.String => KnownType.String,
                    _ => KnownType.Unknown
                };
            }

            public KnownType GetSystemType()
            {
                return KnownType.SystemType;
            }

            public KnownType GetSZArrayType(KnownType elementType)
            {
                return KnownType.Unknown;
            }

            public KnownType GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
            {
                return KnownType.Unknown;
            }

            public KnownType GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            {
                return KnownType.Unknown;
            }

            public KnownType GetTypeFromSerializedName(string name)
            {
                return KnownType.Unknown;
            }

            public PrimitiveTypeCode GetUnderlyingEnumType(KnownType type)
            {
                throw new BadImageFormatException("Unexpectedly got an enum parameter for an attribute.");
            }

            public bool IsSystemType(KnownType type)
            {
                return type == KnownType.SystemType;
            }
        }

        private class UnusedGenericContext { }

        private class NotSupportedTypeException : Exception
        {
            public string Type { get; private set; }
            public NotSupportedTypeException(string type) { this.Type = type; }
        }

        private class C99TypeProvider : ISignatureTypeProvider<string, UnusedGenericContext>
        {
            public string GetArrayType(string elementType, ArrayShape shape)
            {
                throw new NotSupportedTypeException(elementType);
            }

            public string GetByReferenceType(string elementType)
            {
                throw new NotSupportedTypeException(elementType);
            }

            public string GetFunctionPointerType(MethodSignature<string> signature)
            {
                throw new NotImplementedException();
            }

            public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
            {
                throw new NotSupportedTypeException($"Generic - {genericType}");
            }

            public string GetGenericMethodParameter(UnusedGenericContext genericContext, int index)
            {
                throw new NotSupportedTypeException($"Generic - {index}");
            }

            public string GetGenericTypeParameter(UnusedGenericContext genericContext, int index)
            {
                throw new NotSupportedTypeException($"Generic - {index}");
            }

            public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired)
            {
                throw new NotSupportedTypeException($"{modifier} {unmodifiedType}");
            }

            public string GetPinnedType(string elementType)
            {
                throw new NotSupportedTypeException($"Pinned - {elementType}");
            }

            public string GetPointerType(string elementType)
            {
                return elementType + "*";
            }

            public string GetPrimitiveType(PrimitiveTypeCode typeCode)
            {
                return typeCode switch
                {
                    PrimitiveTypeCode.SByte => "int8_t",
                    PrimitiveTypeCode.Byte => "uint8_t",
                    PrimitiveTypeCode.Int16 => "int16_t",
                    PrimitiveTypeCode.UInt16 => "uint16_t",
                    PrimitiveTypeCode.Int32 => "int32_t",
                    PrimitiveTypeCode.UInt32 => "uint32_t",
                    PrimitiveTypeCode.Int64 => "int64_t",
                    PrimitiveTypeCode.UInt64 => "uint64_t",
                    PrimitiveTypeCode.IntPtr => "intptr_t",
                    PrimitiveTypeCode.UIntPtr => "uintptr_t",
                    PrimitiveTypeCode.Single => "float",
                    PrimitiveTypeCode.Double => "double",
                    PrimitiveTypeCode.Void => "void",
                    _ => throw new NotSupportedTypeException(nameof(typeCode))
                };
            }

            public string GetSZArrayType(string elementType)
            {
                throw new NotSupportedTypeException($"Array - {elementType}");
            }

            public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
            {
                throw new NotSupportedTypeException("Non-primitive");
            }

            public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            {
                throw new NotSupportedTypeException("Non-primitive");
            }

            public string GetTypeFromSpecification(MetadataReader reader, UnusedGenericContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
            {
                throw new NotSupportedTypeException("Non-primitive");
            }
        }
    }
}