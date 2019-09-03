using System;
using System.CodeDom;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CSharp;
using Mono.Cecil;
using Mono.Cecil.Rocks;
using ICustomAttributeProvider = Mono.Cecil.ICustomAttributeProvider;
using TypeAttributes = System.Reflection.TypeAttributes;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace PublicApiGenerator
{
public static class RoslynApiGenerator
    {
        static readonly string[] defaultWhitelistedNamespacePrefixes = new string[0];

        public static string GeneratePublicApi(Assembly assembly, Type[] includeTypes = null, bool shouldIncludeAssemblyAttributes = true, string[] whitelistedNamespacePrefixes = null, string[] excludeAttributes = null)
        {
            var attributesToExclude = excludeAttributes == null
                ? SkipAttributeNames
                : new HashSet<string>(excludeAttributes.Union(SkipAttributeNames));

            using (var assemblyResolver = new DefaultAssemblyResolver())
            {
                var assemblyPath = assembly.Location;
                assemblyResolver.AddSearchDirectory(Path.GetDirectoryName(assemblyPath));
                assemblyResolver.AddSearchDirectory(AppDomain.CurrentDomain.BaseDirectory);

                var readSymbols = File.Exists(Path.ChangeExtension(assemblyPath, ".pdb"));
                using (var asm = AssemblyDefinition.ReadAssembly(assemblyPath, new ReaderParameters(ReadingMode.Deferred)
                {
                    ReadSymbols = readSymbols,
                    AssemblyResolver = assemblyResolver
                }))
                {
                    var publicApiForAssembly = CreatePublicApiForAssembly(asm, assemblyPath, tr => includeTypes == null || includeTypes.Any(t => t.FullName == tr.FullName && t.Assembly.FullName == tr.Module.Assembly.FullName),
                        shouldIncludeAssemblyAttributes, whitelistedNamespacePrefixes ?? defaultWhitelistedNamespacePrefixes, attributesToExclude);
                    return RemoveUnnecessaryWhiteSpace(publicApiForAssembly);
                }
            }
        }

        static string RemoveUnnecessaryWhiteSpace(string publicApi)
        {
            return string.Join(Environment.NewLine, publicApi.Split(new[]
                {
                    Environment.NewLine
                }, StringSplitOptions.RemoveEmptyEntries)
                .Where(l => !string.IsNullOrWhiteSpace(l))
            );
        }

        // TODO: Assembly references?
        // TODO: Better handle namespaces - using statements? - requires non-qualified type names
        static string CreatePublicApiForAssembly(AssemblyDefinition assembly, string assemblyPath, Func<TypeDefinition, bool> shouldIncludeType, bool shouldIncludeAssemblyAttributes, string[] whitelistedNamespacePrefixes, HashSet<string> excludeAttributes)
        {
            var reference = MetadataReference.CreateFromFile(assemblyPath);
            var compilation = CSharpCompilation.Create(null)
                .AddReferences(reference)
                .AddReferences(GetGlobalReferences());

            var publicApiBuilder = new StringBuilder();
            
            var assemblySymbol = (IAssemblySymbol)compilation.GetAssemblyOrModuleSymbol(reference);
            var visitor = new MyVisitor(publicApiBuilder);
            assemblySymbol.Accept(visitor);
            
            
            var cgo = new CodeGeneratorOptions
            {
                BracingStyle = "C",
                BlankLinesBetweenMembers = false,
                VerbatimOrder = false,
                IndentString = "    "
            };

            using (var provider = new CSharpCodeProvider())
            {
                var compileUnit = new CodeCompileUnit();
                if (shouldIncludeAssemblyAttributes && assembly.HasCustomAttributes)
                {
                    PopulateCustomAttributes(assembly, compileUnit.AssemblyCustomAttributes, excludeAttributes);
                }

                var publicTypes = assembly.Modules.SelectMany(m => m.GetTypes())
                    .Where(t => !t.IsNested && ShouldIncludeType(t) && shouldIncludeType(t))
                    .OrderBy(t => t.FullName, StringComparer.Ordinal);
                foreach (var publicType in publicTypes)
                {
                    var @namespace = compileUnit.Namespaces.Cast<CodeNamespace>()
                        .FirstOrDefault(n => n.Name == publicType.Namespace);
                    if (@namespace == null)
                    {
                        @namespace = new CodeNamespace(publicType.Namespace);
                        compileUnit.Namespaces.Add(@namespace);
                    }

                    var typeDeclaration = CreateTypeDeclaration(publicType, whitelistedNamespacePrefixes, excludeAttributes);
                    @namespace.Types.Add(typeDeclaration);
                }

                using (var writer = new StringWriter())
                {
                    provider.GenerateCodeFromCompileUnit(compileUnit, writer, cgo);
                    var typeDeclarationText = NormaliseGeneratedCode(writer);
                    publicApiBuilder.AppendLine(typeDeclarationText);
                }
            }
            return NormaliseLineEndings(publicApiBuilder.ToString().Trim());
        }
        
        private static IEnumerable<MetadataReference> GetGlobalReferences()
        {
            var assemblies = new[]
            {
                typeof(System.Object).Assembly, //mscorlib
            };

            var refs = from a in assemblies
                select MetadataReference.CreateFromFile(a.Location);

            return refs.ToList();
        }
        
        class MyVisitor : SymbolVisitor
        {
            private StringBuilder builder;

            public MyVisitor(StringBuilder builder)
            {
                this.builder = builder;
            }
            
            public override void VisitAssembly(IAssemblySymbol symbol)
            {
                foreach (var child in symbol.GetAttributes())
                {
                    builder.AppendLine($"[assembly: {child.ToString()}]");
                }

                symbol.GlobalNamespace.Accept(this);

                foreach (var module in symbol.Modules)
                {
                    module.Accept(this);
                }
            }

            public override void VisitModule(IModuleSymbol symbol)
            {
                foreach (var child in symbol.GetAttributes())
                {
                    
                }
                
                
            }

            public override void VisitNamespace(INamespaceSymbol symbol)
            {
                foreach (var child in symbol.GetMembers())
                {
                    child.Accept(this);
                }
            }

            public override void VisitNamedType(INamedTypeSymbol symbol)
            {
                foreach (var child in symbol.GetMembers())
                {
                    child.Accept(this);
                }
            }

            public override void VisitMethod(IMethodSymbol symbol)
            {
                foreach (var item in symbol.Parameters)
                {
                    Console.WriteLine(item.Name);
                }
            }
        }


        static string NormaliseLineEndings(string value)
        {
            return Regex.Replace(value, @"\r\n|\n\r|\r|\n", Environment.NewLine);
        }

        static bool IsDelegate(TypeDefinition publicType)
        {
            return publicType.BaseType != null && publicType.BaseType.FullName == "System.MulticastDelegate";
        }

        static bool ShouldIncludeType(TypeDefinition t)
        {
            return (t.IsPublic || t.IsNestedPublic || t.IsNestedFamily) && !IsCompilerGenerated(t);
        }

        static bool ShouldIncludeMember(IMemberDefinition m, string[] whitelistedNamespacePrefixes)
        {
            return !IsCompilerGenerated(m) && !IsDotNetTypeMember(m, whitelistedNamespacePrefixes) && !(m is FieldDefinition);
        }

        static bool IsCompilerGenerated(IMemberDefinition m)
        {
            return m.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute");
        }

        static bool IsDotNetTypeMember(IMemberDefinition m, string[] whitelistedNamespacePrefixes)
        {
            if (m.DeclaringType?.FullName == null)
                return false;

            return (m.DeclaringType.FullName.StartsWith("System") || m.DeclaringType.FullName.StartsWith("Microsoft"))
                && !whitelistedNamespacePrefixes.Any(prefix => m.DeclaringType.FullName.StartsWith(prefix));
        }

        static void AddMemberToTypeDeclaration(CodeTypeDeclaration typeDeclaration,
            IMemberDefinition memberInfo,
            HashSet<string> excludeAttributes)
        {
            if (memberInfo is MethodDefinition methodDefinition)
            {
                if (methodDefinition.IsConstructor)
                    AddCtorToTypeDeclaration(typeDeclaration, methodDefinition, excludeAttributes);
                else
                    AddMethodToTypeDeclaration(typeDeclaration, methodDefinition, excludeAttributes);
            }
            else if (memberInfo is PropertyDefinition propertyDefinition)
            {
                AddPropertyToTypeDeclaration(typeDeclaration, propertyDefinition, excludeAttributes);
            }
            else if (memberInfo is EventDefinition eventDefinition)
            {
                typeDeclaration.Members.Add(GenerateEvent(eventDefinition, excludeAttributes));
            }
            else if (memberInfo is FieldDefinition fieldDefinition)
            {
                AddFieldToTypeDeclaration(typeDeclaration, fieldDefinition, excludeAttributes);
            }
        }

        static string NormaliseGeneratedCode(StringWriter writer)
        {
            var gennedClass = writer.ToString();
            const string autoGeneratedHeader = @"^//-+\s*$.*^//-+\s*$";
            const string emptyGetSet = @"\s+{\s+get\s+{\s+}\s+set\s+{\s+}\s+}";
            const string emptyGet = @"\s+{\s+get\s+{\s+}\s+}";
            const string emptySet = @"\s+{\s+set\s+{\s+}\s+}";
            const string getSet = @"\s+{\s+get;\s+set;\s+}";
            const string get = @"\s+{\s+get;\s+}";
            const string set = @"\s+{\s+set;\s+}";
            gennedClass = Regex.Replace(gennedClass, autoGeneratedHeader, string.Empty,
                RegexOptions.IgnorePatternWhitespace | RegexOptions.Multiline | RegexOptions.Singleline);
            gennedClass = Regex.Replace(gennedClass, emptyGetSet, " { get; set; }", RegexOptions.IgnorePatternWhitespace);
            gennedClass = Regex.Replace(gennedClass, getSet, " { get; set; }", RegexOptions.IgnorePatternWhitespace);
            gennedClass = Regex.Replace(gennedClass, emptyGet, " { get; }", RegexOptions.IgnorePatternWhitespace);
            gennedClass = Regex.Replace(gennedClass, emptySet, " { set; }", RegexOptions.IgnorePatternWhitespace);
            gennedClass = Regex.Replace(gennedClass, get, " { get; }", RegexOptions.IgnorePatternWhitespace);
            gennedClass = Regex.Replace(gennedClass, set, " { set; }", RegexOptions.IgnorePatternWhitespace);
            gennedClass = Regex.Replace(gennedClass, @"\s+{\s+}", " { }", RegexOptions.IgnorePatternWhitespace);
            gennedClass = Regex.Replace(gennedClass, @"\)\s+;", ");", RegexOptions.IgnorePatternWhitespace);
            return gennedClass;
        }

        static CodeTypeDeclaration CreateTypeDeclaration(TypeDefinition publicType, string[] whitelistedNamespacePrefixes, HashSet<string> excludeAttributes)
        {
            if (IsDelegate(publicType))
                return CreateDelegateDeclaration(publicType, excludeAttributes);

            var @static = false;
            TypeAttributes attributes = 0;
            if (publicType.IsPublic || publicType.IsNestedPublic)
                attributes |= TypeAttributes.Public;
            if (publicType.IsNestedFamily)
                attributes |= TypeAttributes.NestedFamily;
            if (publicType.IsSealed && !publicType.IsAbstract)
                attributes |= TypeAttributes.Sealed;
            else if (!publicType.IsSealed && publicType.IsAbstract && !publicType.IsInterface)
                attributes |= TypeAttributes.Abstract;
            else if (publicType.IsSealed && publicType.IsAbstract)
                @static = true;

            // Static support is a hack. CodeDOM does support it, and this isn't
            // correct C#, but it's good enough for our API outline
            var name = publicType.Name;

            var isStruct = publicType.IsValueType && !publicType.IsPrimitive && !publicType.IsEnum;
            
            var @readonly = isStruct && publicType.CustomAttributes.Any(a =>
                                 a.AttributeType.FullName == "System.Runtime.CompilerServices.IsReadOnlyAttribute");

            var index = name.IndexOf('`');
            if (index != -1)
                name = name.Substring(0, index);

            var declarationName = string.Empty;
            if (@readonly)
                declarationName += "readonly ";
            if (@static)
                declarationName += "static ";

            declarationName += name;
            
            var declaration = new CodeTypeDeclaration(declarationName)
            {
                CustomAttributes = CreateCustomAttributes(publicType, excludeAttributes),
                // TypeAttributes must be specified before the IsXXX as they manipulate TypeAttributes!
                TypeAttributes = attributes,
                IsClass = publicType.IsClass,
                IsEnum = publicType.IsEnum,
                IsInterface = publicType.IsInterface,
                IsStruct = isStruct,
            };

            if (declaration.IsInterface && publicType.BaseType != null)
                throw new NotImplementedException("Base types for interfaces needs testing");

            PopulateGenericParameters(publicType, declaration.TypeParameters, excludeAttributes);

            if (publicType.BaseType != null && ShouldOutputBaseType(publicType))
            {
                if (publicType.BaseType.FullName == "System.Enum")
                {
                    var underlyingType = publicType.GetEnumUnderlyingType();
                    if (underlyingType.FullName != "System.Int32")
                        declaration.BaseTypes.Add(CreateCodeTypeReference(underlyingType));
                }
                else
                    declaration.BaseTypes.Add(CreateCodeTypeReference(publicType.BaseType));
            }
            foreach(var @interface in publicType.Interfaces.OrderBy(i => i.InterfaceType.FullName, StringComparer.Ordinal)
                .Select(t => new { Reference = t, Definition = t.InterfaceType.Resolve() })
                .Where(t => ShouldIncludeType(t.Definition))
                .Select(t => t.Reference))
                declaration.BaseTypes.Add(CreateCodeTypeReference(@interface.InterfaceType));

            foreach (var memberInfo in publicType.GetMembers().Where(memberDefinition => ShouldIncludeMember(memberDefinition, whitelistedNamespacePrefixes)).OrderBy(m => m.Name, StringComparer.Ordinal))
                AddMemberToTypeDeclaration(declaration, memberInfo, excludeAttributes);

            // Fields should be in defined order for an enum
            var fields = !publicType.IsEnum
                ? publicType.Fields.OrderBy(f => f.Name, StringComparer.Ordinal)
                : (IEnumerable<FieldDefinition>)publicType.Fields;
            foreach (var field in fields)
                AddMemberToTypeDeclaration(declaration, field, excludeAttributes);

            foreach (var nestedType in publicType.NestedTypes.Where(ShouldIncludeType).OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                var nestedTypeDeclaration = CreateTypeDeclaration(nestedType, whitelistedNamespacePrefixes, excludeAttributes);
                declaration.Members.Add(nestedTypeDeclaration);
            }

            return declaration;
        }

        static CodeTypeDeclaration CreateDelegateDeclaration(TypeDefinition publicType, HashSet<string> excludeAttributes)
        {
            var invokeMethod = publicType.Methods.Single(m => m.Name == "Invoke");
            var name = publicType.Name;
            var index = name.IndexOf('`');
            if (index != -1)
                name = name.Substring(0, index);
            var declaration = new CodeTypeDelegate(name)
            {
                Attributes = MemberAttributes.Public,
                CustomAttributes = CreateCustomAttributes(publicType, excludeAttributes),
                ReturnType = CreateCodeTypeReference(invokeMethod.ReturnType),
            };

            // CodeDOM. No support. Return type attributes.
            PopulateCustomAttributes(invokeMethod.MethodReturnType, declaration.CustomAttributes, type => ModifyCodeTypeReference(type, "return:"), excludeAttributes);
            PopulateGenericParameters(publicType, declaration.TypeParameters, excludeAttributes);
            PopulateMethodParameters(invokeMethod, declaration.Parameters, excludeAttributes);

            // Of course, CodeDOM doesn't support generic type parameters for delegates. Of course.
            if (declaration.TypeParameters.Count > 0)
            {
                var parameterNames = from parameterType in declaration.TypeParameters.Cast<CodeTypeParameter>()
                    select parameterType.Name;
                declaration.Name = string.Format(CultureInfo.InvariantCulture, "{0}<{1}>", declaration.Name, string.Join(", ", parameterNames));
            }

            return declaration;
        }

        static bool ShouldOutputBaseType(TypeDefinition publicType)
        {
            return publicType.BaseType.FullName != "System.Object" && publicType.BaseType.FullName != "System.ValueType";
        }

        static void PopulateGenericParameters(IGenericParameterProvider publicType, CodeTypeParameterCollection parameters, HashSet<string> excludeAttributes)
        {
            foreach (var parameter in publicType.GenericParameters)
            {
                // A little hacky. Means we get "in" and "out" prefixed on any constraints, but it's either that
                // or add it as a custom attribute
                var name = parameter.Name;
                if (parameter.IsCovariant)
                    name = "out " + name;
                if (parameter.IsContravariant)
                    name = "in " + name;

                var attributeCollection = new CodeAttributeDeclarationCollection();
                if (parameter.HasCustomAttributes)
                {
                    PopulateCustomAttributes(parameter, attributeCollection, excludeAttributes);
                }

                var typeParameter = new CodeTypeParameter(name)
                {
                    HasConstructorConstraint =
                        parameter.HasDefaultConstructorConstraint && !parameter.HasNotNullableValueTypeConstraint
                };

                typeParameter.CustomAttributes.AddRange(attributeCollection.OfType<CodeAttributeDeclaration>().ToArray());

                if (parameter.HasNotNullableValueTypeConstraint)
                    typeParameter.Constraints.Add(" struct"); // Extra space is a hack!
                if (parameter.HasReferenceTypeConstraint)
                    typeParameter.Constraints.Add(" class");
                foreach (var constraint in parameter.Constraints.Where(t => t.FullName != "System.ValueType"))
                {
                    // for generic constraints like IEnumerable<T> call to GetElementType() returns TypeReference with Name = !0
                    typeParameter.Constraints.Add(CreateCodeTypeReference(constraint/*.GetElementType()*/));
                }
                parameters.Add(typeParameter);
            }
        }

        static CodeAttributeDeclarationCollection CreateCustomAttributes(ICustomAttributeProvider type,
            HashSet<string> excludeAttributes)
        {
            var attributes = new CodeAttributeDeclarationCollection();
            PopulateCustomAttributes(type, attributes, excludeAttributes);
            return attributes;
        }

        static void PopulateCustomAttributes(ICustomAttributeProvider type,
            CodeAttributeDeclarationCollection attributes,
            HashSet<string> excludeAttributes)
        {
            PopulateCustomAttributes(type, attributes, ctr => ctr, excludeAttributes);
        }

        static void PopulateCustomAttributes(ICustomAttributeProvider type,
            CodeAttributeDeclarationCollection attributes,
            Func<CodeTypeReference, CodeTypeReference> codeTypeModifier,
            HashSet<string> excludeAttributes)
        {
            foreach (var customAttribute in type.CustomAttributes.Where(t => ShouldIncludeAttribute(t, excludeAttributes)).OrderBy(a => a.AttributeType.FullName, StringComparer.Ordinal).ThenBy(a => ConvertAttributeToCode(codeTypeModifier, a), StringComparer.Ordinal))
            {
                var attribute = GenerateCodeAttributeDeclaration(codeTypeModifier, customAttribute);
                attributes.Add(attribute);
            }
        }

        static CodeAttributeDeclaration GenerateCodeAttributeDeclaration(Func<CodeTypeReference, CodeTypeReference> codeTypeModifier, CustomAttribute customAttribute)
        {
            var attribute = new CodeAttributeDeclaration(codeTypeModifier(CreateCodeTypeReference(customAttribute.AttributeType)));
            foreach (var arg in customAttribute.ConstructorArguments)
            {
                attribute.Arguments.Add(new CodeAttributeArgument(CreateInitialiserExpression(arg)));
            }
            foreach (var field in customAttribute.Fields.OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                attribute.Arguments.Add(new CodeAttributeArgument(field.Name, CreateInitialiserExpression(field.Argument)));
            }
            foreach (var property in customAttribute.Properties.OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                attribute.Arguments.Add(new CodeAttributeArgument(property.Name, CreateInitialiserExpression(property.Argument)));
            }
            return attribute;
        }

        // Litee: This method is used for additional sorting of custom attributes when multiple values are allowed
        static string ConvertAttributeToCode(Func<CodeTypeReference, CodeTypeReference> codeTypeModifier, CustomAttribute customAttribute)
        {
            using (var provider = new CSharpCodeProvider())
            {
                var cgo = new CodeGeneratorOptions
                {
                    BracingStyle = "C",
                    BlankLinesBetweenMembers = false,
                    VerbatimOrder = false
                };
                var attribute = GenerateCodeAttributeDeclaration(codeTypeModifier, customAttribute);
                var declaration = new CodeTypeDeclaration("DummyClass")
                {
                    CustomAttributes = new CodeAttributeDeclarationCollection(new[] { attribute }),
                };
                using (var writer = new StringWriter())
                {
                    provider.GenerateCodeFromType(declaration, writer, cgo);
                    return writer.ToString();
                }
            }
        }

        static readonly HashSet<string> SkipAttributeNames = new HashSet<string>
        {
            "System.CodeDom.Compiler.GeneratedCodeAttribute",
            "System.ComponentModel.EditorBrowsableAttribute",
            "System.Runtime.CompilerServices.AsyncStateMachineAttribute",
            "System.Runtime.CompilerServices.CompilerGeneratedAttribute",
            "System.Runtime.CompilerServices.CompilationRelaxationsAttribute",
            "System.Runtime.CompilerServices.ExtensionAttribute",
            "System.Runtime.CompilerServices.RuntimeCompatibilityAttribute",
            "System.Runtime.CompilerServices.IteratorStateMachineAttribute",
            "System.Runtime.CompilerServices.IsReadOnlyAttribute",
            "System.Reflection.DefaultMemberAttribute",
            "System.Diagnostics.DebuggableAttribute",
            "System.Diagnostics.DebuggerNonUserCodeAttribute",
            "System.Diagnostics.DebuggerStepThroughAttribute",
            "System.Reflection.AssemblyCompanyAttribute",
            "System.Reflection.AssemblyConfigurationAttribute",
            "System.Reflection.AssemblyCopyrightAttribute",
            "System.Reflection.AssemblyDescriptionAttribute",
            "System.Reflection.AssemblyFileVersionAttribute",
            "System.Reflection.AssemblyInformationalVersionAttribute",
            "System.Reflection.AssemblyProductAttribute",
            "System.Reflection.AssemblyTitleAttribute",
            "System.Reflection.AssemblyTrademarkAttribute"
        };

        static bool ShouldIncludeAttribute(CustomAttribute attribute, HashSet<string> excludeAttributes)
        {
            var attributeTypeDefinition = attribute.AttributeType.Resolve();
            return attributeTypeDefinition != null && !excludeAttributes.Contains(attribute.AttributeType.FullName) && (attributeTypeDefinition.IsPublic || attributeTypeDefinition.FullName == "System.Runtime.CompilerServices.NullableAttribute");
        }

        static CodeExpression CreateInitialiserExpression(CustomAttributeArgument attributeArgument)
        {
            if (attributeArgument.Value is CustomAttributeArgument customAttributeArgument)
            {
                return CreateInitialiserExpression(customAttributeArgument);
            }

            if (attributeArgument.Value is CustomAttributeArgument[] customAttributeArguments)
            {
                var initialisers = from argument in customAttributeArguments
                                   select CreateInitialiserExpression(argument);
                return new CodeArrayCreateExpression(CreateCodeTypeReference(attributeArgument.Type), initialisers.ToArray());
            }

            var type = attributeArgument.Type.Resolve();
            var value = attributeArgument.Value;
            if (type.BaseType != null && type.BaseType.FullName == "System.Enum")
            {
                var originalValue = Convert.ToInt64(value);
                if (type.CustomAttributes.Any(a => a.AttributeType.FullName == "System.FlagsAttribute"))
                {
                    //var allFlags = from f in type.Fields
                    //    where f.Constant != null
                    //    let v = Convert.ToInt64(f.Constant)
                    //    where v == 0 || (originalValue & v) != 0
                    //    select (CodeExpression)new CodeFieldReferenceExpression(typeExpression, f.Name);
                    //return allFlags.Aggregate((current, next) => new CodeBinaryOperatorExpression(current, CodeBinaryOperatorType.BitwiseOr, next));

                    // I'd rather use the above, as it's just using the CodeDOM, but it puts
                    // brackets around each CodeBinaryOperatorExpression
                    var flags = from f in type.Fields
                                where f.Constant != null
                                let v = Convert.ToInt64(f.Constant)
                                where v == 0 || (originalValue & v) != 0
                                select type.FullName + "." + f.Name;
                    return new CodeSnippetExpression(flags.Aggregate((current, next) => current + " | " + next));
                }

                var allFlags = from f in type.Fields
                               where f.Constant != null
                               let v = Convert.ToInt64(f.Constant)
                               where v == originalValue
                               select new CodeFieldReferenceExpression(new CodeTypeReferenceExpression(CreateCodeTypeReference(type)), f.Name);
                return allFlags.FirstOrDefault();
            }

            if (type.FullName == "System.Type" && value is TypeReference typeRef)
            {
                return new CodeTypeOfExpression(CreateCodeTypeReference(typeRef));
            }

            if (value is string)
            {
                // CodeDOM outputs a verbatim string. Any string with \n is treated as such, so normalize
                // it to make it easier for comparisons
                value = Regex.Replace((string)value, @"\n", "\\n");
                value = Regex.Replace((string)value, @"\r\n|\r\\n", "\\r\\n");
            }

            return new CodePrimitiveExpression(value);
        }

        static void AddCtorToTypeDeclaration(CodeTypeDeclaration typeDeclaration, MethodDefinition member, HashSet<string> excludeAttributes)
        {
            if (member.IsAssembly || member.IsPrivate)
                return;

            var method = new CodeConstructor
            {
                CustomAttributes = CreateCustomAttributes(member, excludeAttributes),
                Name = member.Name,
                Attributes = GetMethodAttributes(member)
            };
            PopulateMethodParameters(member, method.Parameters, excludeAttributes);

            typeDeclaration.Members.Add(method);
        }

        static readonly IDictionary<string, string> OperatorNameMap = new Dictionary<string, string>
        {
            { "op_Addition", "+" },
            { "op_UnaryPlus", "+" },
            { "op_Subtraction", "-" },
            { "op_UnaryNegation", "-" },
            { "op_Multiply", "*" },
            { "op_Division", "/" },
            { "op_Modulus", "%" },
            { "op_Increment", "++" },
            { "op_Decrement", "--" },
            { "op_OnesComplement", "~" },
            { "op_LogicalNot", "!" },
            { "op_BitwiseAnd", "&" },
            { "op_BitwiseOr", "|" },
            { "op_ExclusiveOr", "^" },
            { "op_LeftShift", "<<" },
            { "op_RightShift", ">>" },
            { "op_Equality", "==" },
            { "op_Inequality", "!=" },
            { "op_GreaterThan", ">" },
            { "op_GreaterThanOrEqual", ">=" },
            { "op_LessThan", "<" },
            { "op_LessThanOrEqual", "<=" }
        };

        static void AddMethodToTypeDeclaration(CodeTypeDeclaration typeDeclaration, MethodDefinition member, HashSet<string> excludeAttributes)
        {
            if (member.IsAssembly || member.IsPrivate) return;

            if (member.IsSpecialName && !member.Name.StartsWith("op_")) return;

            var memberName = member.Name;
            // ReSharper disable once InlineOutVariableDeclaration
            if (OperatorNameMap.TryGetValue(memberName, out string mappedMemberName))
            {
                memberName = mappedMemberName;
            }

            var returnType = CreateCodeTypeReference(member.ReturnType);

            var method = new CodeMemberMethod
            {
                Name = memberName,
                Attributes = GetMethodAttributes(member),
                CustomAttributes = CreateCustomAttributes(member, excludeAttributes),
                ReturnType = returnType,
            };
            PopulateCustomAttributes(member.MethodReturnType, method.ReturnTypeCustomAttributes, excludeAttributes);
            PopulateGenericParameters(member, method.TypeParameters, excludeAttributes);
            PopulateMethodParameters(member, method.Parameters, excludeAttributes, IsExtensionMethod(member));

            typeDeclaration.Members.Add(method);
        }

        static bool IsExtensionMethod(ICustomAttributeProvider method)
        {
            return method.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.ExtensionAttribute");
        }

        static void PopulateMethodParameters(IMethodSignature member,
            CodeParameterDeclarationExpressionCollection parameters,
            HashSet<string> excludeAttributes,
            bool isExtension = false)
        {
            foreach (var parameter in member.Parameters)
            {
                FieldDirection direction = 0;
                if (parameter.IsOut)
                    direction |= FieldDirection.Out;
                else if (parameter.ParameterType.IsByReference)
                    direction |= FieldDirection.Ref;

                var parameterType = parameter.ParameterType;
                if (parameterType is RequiredModifierType requiredModifierType)
                {
                    parameterType = requiredModifierType.ElementType;
                }
                // order is crucial because a RequiredModifierType can be a ByReferenceType
                if (parameterType is ByReferenceType byReferenceType)
                {
                    parameterType = byReferenceType.ElementType;
                }

                var type = CreateCodeTypeReference(parameterType);

                if (isExtension)
                {
                    type = ModifyCodeTypeReference(type, "this");
                    isExtension = false;
                }

                // special case of ref is in
                // TODO: Move CustomAttributes.Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.IsReadOnlyAttribute") to extension method once other PR is merged
                if (parameter.CustomAttributes.Any(a =>
                        a.AttributeType.FullName == "System.Runtime.CompilerServices.IsReadOnlyAttribute"))
                {
                    type = ModifyCodeTypeReference(type, "in");
                    direction = FieldDirection.In;
                }

                var name = parameter.HasConstant
                    ? string.Format(CultureInfo.InvariantCulture, "{0} = {1}", parameter.Name, FormatParameterConstant(parameter))
                    : parameter.Name;
                var expression = new CodeParameterDeclarationExpression(type, name)
                {
                    Direction = direction,
                    CustomAttributes = CreateCustomAttributes(parameter, excludeAttributes)
                };
                parameters.Add(expression);
            }
        }

        static object FormatParameterConstant(IConstantProvider parameter)
        {
            return parameter.Constant is string ? string.Format(CultureInfo.InvariantCulture, "\"{0}\"", parameter.Constant) : (parameter.Constant ?? "null");
        }

        static MemberAttributes GetMethodAttributes(MethodDefinition method)
        {
            MemberAttributes access = 0;
            if (method.IsFamily || method.IsFamilyOrAssembly)
                access = MemberAttributes.Family;
            if (method.IsPublic)
                access = MemberAttributes.Public;
            if (method.IsAssembly)
                access = MemberAttributes.Assembly;

            MemberAttributes scope = 0;
            if (method.IsStatic)
                scope |= MemberAttributes.Static;
            if (method.IsFinal || !method.IsVirtual)
                scope |= MemberAttributes.Final;
            if (method.IsAbstract)
                scope |= MemberAttributes.Abstract;
            if (method.IsVirtual && !method.IsNewSlot)
                scope |= MemberAttributes.Override;

            MemberAttributes vtable = 0;
            if (IsHidingMethod(method))
                vtable = MemberAttributes.New;

            return access | scope | vtable;
        }

        static bool IsHidingMethod(MethodDefinition method)
        {
            var typeDefinition = method.DeclaringType;

            // If we're an interface, just try and find any method with the same signature
            // in any of the interfaces that we implement
            if (typeDefinition.IsInterface)
            {
                var interfaceMethods = from @interfaceReference in typeDefinition.Interfaces
                    let interfaceDefinition = @interfaceReference.InterfaceType.Resolve()
                    where interfaceDefinition != null
                    select interfaceDefinition.Methods;

                return interfaceMethods.Any(ms => MetadataResolver.GetMethod(ms, method) != null);
            }

            // If we're not an interface, find a base method that isn't virtual
            return !method.IsVirtual && GetBaseTypes(typeDefinition).Any(d => MetadataResolver.GetMethod(d.Methods, method) != null);
        }

        static IEnumerable<TypeDefinition> GetBaseTypes(TypeDefinition type)
        {
            var baseType = type.BaseType;
            while (baseType != null)
            {
                var definition = baseType.Resolve();
                if (definition == null)
                    yield break;
                yield return definition;

                baseType = baseType.DeclaringType;
            }
        }

        static void AddPropertyToTypeDeclaration(CodeTypeDeclaration typeDeclaration, PropertyDefinition member, HashSet<string> excludeAttributes)
        {
            var getterAttributes = member.GetMethod != null ? GetMethodAttributes(member.GetMethod) : 0;
            var setterAttributes = member.SetMethod != null ? GetMethodAttributes(member.SetMethod) : 0;

            if (!HasVisiblePropertyMethod(getterAttributes) && !HasVisiblePropertyMethod(setterAttributes))
                return;

            var propertyAttributes = GetPropertyAttributes(getterAttributes, setterAttributes);

            var propertyType = member.PropertyType.IsGenericParameter
                ? new CodeTypeReference(member.PropertyType.Name)
                : CreateCodeTypeReference(member.PropertyType);

            var property = new CodeMemberProperty
            {
                Name = member.Name,
                Type = propertyType,
                Attributes = propertyAttributes,
                CustomAttributes = CreateCustomAttributes(member, excludeAttributes),
                HasGet = member.GetMethod != null && HasVisiblePropertyMethod(getterAttributes),
                HasSet = member.SetMethod != null && HasVisiblePropertyMethod(setterAttributes)
            };

            // Here's a nice hack, because hey, guess what, the CodeDOM doesn't support
            // attributes on getters or setters
            if (member.GetMethod != null && member.GetMethod.HasCustomAttributes)
            {
                PopulateCustomAttributes(member.GetMethod, property.CustomAttributes, type => ModifyCodeTypeReference(type, "get:"), excludeAttributes);
            }
            if (member.SetMethod != null && member.SetMethod.HasCustomAttributes)
            {
                PopulateCustomAttributes(member.SetMethod, property.CustomAttributes, type => ModifyCodeTypeReference(type, "set:"), excludeAttributes);
            }

            foreach (var parameter in member.Parameters)
            {
                property.Parameters.Add(
                    new CodeParameterDeclarationExpression(CreateCodeTypeReference(parameter.ParameterType),
                        parameter.Name));
            }

            // TODO: CodeDOM has no support for different access modifiers for getters and setters
            // TODO: CodeDOM has no support for attributes on setters or getters - promote to property?

            typeDeclaration.Members.Add(property);
        }

        static MemberAttributes GetPropertyAttributes(MemberAttributes getterAttributes, MemberAttributes setterAttributes)
        {
            MemberAttributes access = 0;
            var getterAccess = getterAttributes & MemberAttributes.AccessMask;
            var setterAccess = setterAttributes & MemberAttributes.AccessMask;
            if (getterAccess == MemberAttributes.Public || setterAccess == MemberAttributes.Public)
                access = MemberAttributes.Public;
            else if (getterAccess == MemberAttributes.Family || setterAccess == MemberAttributes.Family)
                access = MemberAttributes.Family;
            else if (getterAccess == MemberAttributes.FamilyAndAssembly || setterAccess == MemberAttributes.FamilyAndAssembly)
                access = MemberAttributes.FamilyAndAssembly;
            else if (getterAccess == MemberAttributes.FamilyOrAssembly || setterAccess == MemberAttributes.FamilyOrAssembly)
                access = MemberAttributes.FamilyOrAssembly;
            else if (getterAccess == MemberAttributes.Assembly || setterAccess == MemberAttributes.Assembly)
                access = MemberAttributes.Assembly;
            else if (getterAccess == MemberAttributes.Private || setterAccess == MemberAttributes.Private)
                access = MemberAttributes.Private;

            // Scope should be the same for getter and setter. If one isn't specified, it'll be 0
            var getterScope = getterAttributes & MemberAttributes.ScopeMask;
            var setterScope = setterAttributes & MemberAttributes.ScopeMask;
            var scope = (MemberAttributes) Math.Max((int) getterScope, (int) setterScope);

            // Vtable should be the same for getter and setter. If one isn't specified, it'll be 0
            var getterVtable = getterAttributes & MemberAttributes.VTableMask;
            var setterVtable = setterAttributes & MemberAttributes.VTableMask;
            var vtable = (MemberAttributes) Math.Max((int) getterVtable, (int) setterVtable);

            return access | scope | vtable;
        }

        static bool HasVisiblePropertyMethod(MemberAttributes attributes)
        {
            var access = attributes & MemberAttributes.AccessMask;
            return access == MemberAttributes.Public ||
                   access == MemberAttributes.Family ||
                   access == MemberAttributes.FamilyOrAssembly;
        }

        static CodeTypeMember GenerateEvent(EventDefinition eventDefinition, HashSet<string> excludeAttributes)
        {
            var @event = new CodeMemberEvent
            {
                Name = eventDefinition.Name,
                Attributes = MemberAttributes.Public | MemberAttributes.Final,
                CustomAttributes = CreateCustomAttributes(eventDefinition, excludeAttributes),
                Type = CreateCodeTypeReference(eventDefinition.EventType)
            };

            return @event;
        }

        static void AddFieldToTypeDeclaration(CodeTypeDeclaration typeDeclaration, FieldDefinition memberInfo, HashSet<string> excludeAttributes)
        {
            if (memberInfo.IsPrivate || memberInfo.IsAssembly || memberInfo.IsSpecialName)
                return;

            MemberAttributes attributes = 0;
            if (memberInfo.HasConstant)
                attributes |= MemberAttributes.Const;
            if (memberInfo.IsFamily || memberInfo.IsFamilyOrAssembly)
                attributes |= MemberAttributes.Family;
            if (memberInfo.IsPublic)
                attributes |= MemberAttributes.Public;
            if (memberInfo.IsStatic && !memberInfo.HasConstant)
                attributes |= MemberAttributes.Static;

            // TODO: Values for readonly fields are set in the ctor
            var codeTypeReference = CreateCodeTypeReference(memberInfo.FieldType);
            if (memberInfo.IsInitOnly)
                codeTypeReference = MakeReadonly(codeTypeReference);
            var field = new CodeMemberField(codeTypeReference, memberInfo.Name)
            {
                Attributes = attributes,
                CustomAttributes = CreateCustomAttributes(memberInfo, excludeAttributes)
            };

            if (memberInfo.HasConstant)
                field.InitExpression = new CodePrimitiveExpression(memberInfo.Constant);

            typeDeclaration.Members.Add(field);
        }

        static CodeTypeReference MakeReadonly(CodeTypeReference typeReference)
        {
            return ModifyCodeTypeReference(typeReference, "readonly");
        }

        static CodeTypeReference ModifyCodeTypeReference(CodeTypeReference typeReference, string modifier)
        {
            using (var provider = new CSharpCodeProvider())
                return new CodeTypeReference(modifier + " " + provider.GetTypeOutput(typeReference));
        }

        static CodeTypeReference CreateCodeTypeReference(TypeReference type)
        {
            var typeName = GetTypeName(type);
            return new CodeTypeReference(typeName, CreateGenericArguments(type));
        }

        static string GetTypeName(TypeReference type)
        {
            if (type.IsGenericParameter)
                return type.Name;

            if (!type.IsNested)
            {
                return (!string.IsNullOrEmpty(type.Namespace) ? (type.Namespace + ".") : "") + type.Name;
            }

            return GetTypeName(type.DeclaringType) + "." + type.Name;
        }

        static CodeTypeReference[] CreateGenericArguments(TypeReference type)
        {
            // ReSharper disable once RedundantEnumerableCastCall
            var genericArgs = type is IGenericInstance instance ? instance.GenericArguments : type.HasGenericParameters ? type.GenericParameters.Cast<TypeReference>() : null;
            if (genericArgs == null) return null;

            var genericArguments = new List<CodeTypeReference>();
            foreach (var argument in genericArgs)
            {
                genericArguments.Add(CreateCodeTypeReference(argument));
            }
            return genericArguments.ToArray();
        }
    }
}