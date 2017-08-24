﻿using System.Collections.Generic;
using System.Linq;
using System.Text;
using CppSharp.AST;
using CppSharp.AST.Extensions;
using CppSharp.Generators.CSharp;

namespace CppSharp.Passes
{
    public class DelegatesPass : TranslationUnitPass
    {
        public DelegatesPass()
        {
            VisitOptions.VisitClassBases = false;
            VisitOptions.VisitFunctionReturnType = false;
            VisitOptions.VisitNamespaceEnums = false;
            VisitOptions.VisitNamespaceTemplates = false;
            VisitOptions.VisitTemplateArguments = false;
        }

        public override bool VisitASTContext(ASTContext context)
        {
            bool result = base.VisitASTContext(context);

            foreach (var typedefDecl in allTypedefs)
                typedefDecl.Namespace.Declarations.Add(typedefDecl);
            allTypedefs.Clear();

            if (!Options.IsCSharpGenerator)
                return result;

            var generatedUnits = context.TranslationUnits.GetGenerated();
            var unit = generatedUnits.LastOrDefault();

            if (unit == null)
                return false;

            foreach (var module in Options.Modules.Where(namespacesDelegates.ContainsKey))
            {
                var @namespace = namespacesDelegates[module];
                @namespace.Namespace.Declarations.Add(@namespace);
            }

            return result;
        }

        public override bool VisitMethodDecl(Method method)
        {
            if (!base.VisitMethodDecl(method) || !method.IsVirtual || method.Ignore)
                return false;

            if (Options.IsCSharpGenerator)
            {
                var module = method.TranslationUnit.Module;
                method.FunctionType = CheckType(method.FunctionType, method);
            }

            return true;
        }

        public override bool VisitFunctionDecl(Function function)
        {
            if (!base.VisitFunctionDecl(function) || function.Ignore)
                return false;

            function.ReturnType = CheckType(function.ReturnType, function);
            return true;
        }

        public override bool VisitParameterDecl(Parameter parameter)
        {
            if (!base.VisitParameterDecl(parameter) || parameter.Namespace == null ||
                parameter.Namespace.Ignore)
                return false;

            parameter.QualifiedType = CheckType(parameter.QualifiedType, parameter);

            return true;
        }

        private QualifiedType CheckType(QualifiedType type, ITypedDecl decl)
        {
            if (type.Type is TypedefType)
                return type;

            var desugared = type.Type.Desugar();
            if (desugared.IsDependent)
                return type;

            Type pointee = desugared.GetPointee() ?? desugared;
            if (pointee is TypedefType)
                return type;

            var functionType = pointee.Desugar() as FunctionType;
            if (functionType == null)
                return type;

            FunctionType newFunctionType = GetNewFunctionType(decl, type);

            var delegateName = GetDelegateName(newFunctionType);
            var typedef = allTypedefs.SingleOrDefault(t => t.Name == delegateName);
            if (typedef != null)
                return new QualifiedType(new TypedefType { Declaration = typedef });

            for (int i = 0; i < functionType.Parameters.Count; i++)
                functionType.Parameters[i].Name = $"_{i}";

            TypedefDecl @delegate = GetDelegate((Declaration) decl, newFunctionType, delegateName);
            return new QualifiedType(new TypedefType { Declaration = @delegate });
        }

        private TypedefDecl GetDelegate(Declaration decl, FunctionType newFunctionType, string delegateName)
        {
            DeclarationContext namespaceDelegates = GetDeclContextForDelegates(decl.Namespace);
            var delegateType = new QualifiedType(new PointerType(new QualifiedType(newFunctionType)));
            var access = decl is Method ? AccessSpecifier.Private : AccessSpecifier.Public;
            var existingDelegate = allTypedefs.SingleOrDefault(t => t.Name == delegateName);
            if (existingDelegate != null)
            {
                // Code to ensure that if a delegate is used for a virtual as well as something else, it finally ends up as public
                if (existingDelegate.Access == AccessSpecifier.Private && access == AccessSpecifier.Public)
                    existingDelegate.Access = access;
                // Code to check if there is an existing delegate with a different calling convention
                // Add the new delegate with calling convention appended to it's name
                if (((FunctionType) existingDelegate.Type.GetPointee()).CallingConvention ==
                    newFunctionType.CallingConvention)
                    return existingDelegate;
                var @delegate = new TypedefDecl
                {
                    Access = access,
                    Name = delegateName + newFunctionType.CallingConvention,
                    Namespace = namespaceDelegates,
                    QualifiedType = delegateType,
                    IsSynthetized = true
                };
                allTypedefs.Add(@delegate);
                return @delegate;
            }
            existingDelegate = new TypedefDecl
                {
                    Access = access,
                    Name = delegateName,
                    Namespace = namespaceDelegates,
                    QualifiedType = delegateType,
                    IsSynthetized = true
                };
            allTypedefs.Add(existingDelegate);
            return existingDelegate;
        }

        private FunctionType GetNewFunctionType(ITypedDecl decl, QualifiedType type)
        {
            var functionType = new FunctionType();
            if (decl is Method method && method.FunctionType == type)
            {
                functionType.Parameters.AddRange(
                    method.GatherInternalParams(Context.ParserOptions.IsItaniumLikeAbi, true));
                functionType.CallingConvention = method.CallingConvention;
                functionType.IsDependent = method.IsDependent;
                functionType.ReturnType = method.ReturnType;
            }
            else
            {
                var funcTypeParam = (FunctionType) decl.Type.Desugar().GetFinalPointee().Desugar();
                functionType.Parameters.AddRange(funcTypeParam.Parameters);
                functionType.CallingConvention = funcTypeParam.CallingConvention;
                functionType.IsDependent = funcTypeParam.IsDependent;
                functionType.ReturnType = funcTypeParam.ReturnType;
            }
            return functionType;
        }

        private DeclarationContext GetDeclContextForDelegates(DeclarationContext @namespace)
        {
            if (Options.IsCLIGenerator)
                return @namespace is Function ? @namespace.Namespace : @namespace;

            var module = @namespace.TranslationUnit.Module;
            if (namespacesDelegates.ContainsKey(module))
                return namespacesDelegates[module];

            Namespace parent = null;
            if (string.IsNullOrEmpty(module.OutputNamespace))
            {
                var groups = module.Units.SelectMany(u => u.Declarations).OfType<Namespace>(
                    ).GroupBy(d => d.Name).Where(g => g.Any(d => d.HasDeclarations)).ToList();
                if (groups.Count == 1)
                    parent = groups.Last().Last();
            }
            if (parent == null)
                parent = module.Units.Last();
            var namespaceDelegates = new Namespace
                {
                    Name = "Delegates",
                    Namespace = parent
                };
            namespacesDelegates.Add(module, namespaceDelegates);
            return namespaceDelegates;
        }

        private string GetDelegateName(FunctionType functionType)
        {
            var typesBuilder = new StringBuilder();
            if (!functionType.ReturnType.Type.IsPrimitiveType(PrimitiveType.Void))
            {
                typesBuilder.Insert(0, functionType.ReturnType.Visit(TypePrinter));
                typesBuilder.Append('_');
            }
            foreach (var parameter in functionType.Parameters)
            {
                typesBuilder.Append(parameter.Visit(TypePrinter));
                typesBuilder.Append('_');
            }
            if (typesBuilder.Length > 0)
                typesBuilder.Remove(typesBuilder.Length - 1, 1);
            var delegateName = FormatTypesStringForIdentifier(typesBuilder);
            if (functionType.ReturnType.Type.IsPrimitiveType(PrimitiveType.Void))
                delegateName.Insert(0, "Action_");
            else
                delegateName.Insert(0, "Func_");

            return delegateName.ToString();
        }

        private static StringBuilder FormatTypesStringForIdentifier(StringBuilder types)
        {
            // TODO: all of this needs proper general fixing by only leaving type names
            return types.Replace("global::System.", string.Empty)
                .Replace("[MarshalAs(UnmanagedType.LPStr)] ", string.Empty)
                .Replace("[MarshalAs(UnmanagedType.LPWStr)] ", string.Empty)
                .Replace("global::", string.Empty).Replace("*", "Ptr")
                .Replace('.', '_').Replace(' ', '_').Replace("::", "_");
        }

        private CSharpTypePrinter TypePrinter
        {
            get
            {
                if (typePrinter == null)
                {
                    typePrinter = new CSharpTypePrinter(Context);
                    typePrinter.PushContext(TypePrinterContextKind.Native);
                    typePrinter.PushMarshalKind(MarshalKind.GenericDelegate);
                }
                return typePrinter;
            }
        }

        private Dictionary<Module, DeclarationContext> namespacesDelegates = new Dictionary<Module, DeclarationContext>();
        private CSharpTypePrinter typePrinter;

        /// <summary>
        /// The generated typedefs. The tree can't be modified while
        /// iterating over it, so we collect all the typedefs and add them at the end.
        /// </summary>
        private readonly List<TypedefDecl> allTypedefs = new List<TypedefDecl>();
    }
}
