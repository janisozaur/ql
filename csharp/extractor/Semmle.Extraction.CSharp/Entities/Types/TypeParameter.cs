using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Semmle.Extraction.Entities;
using System.Collections.Generic;
using System.Linq;

namespace Semmle.Extraction.CSharp.Entities
{
    enum Variance
    {
        None = 0,
        Out = 1,
        In = 2
    }

    class TypeParameter : Type<ITypeParameterSymbol>
    {
        TypeParameter(Context cx, ITypeParameterSymbol init)
            : base(cx, init) { }

        static readonly string valueTypeName = typeof(System.ValueType).ToString();

        public override void Populate()
        {
            var constraints = new TypeParameterConstraints(Context);
            Context.Emit(Tuples.type_parameter_constraints(constraints, this));

            if (symbol.HasReferenceTypeConstraint)
                Context.Emit(Tuples.general_type_parameter_constraints(constraints, 1));

            if (symbol.HasValueTypeConstraint)
                Context.Emit(Tuples.general_type_parameter_constraints(constraints, 2));

            if (symbol.HasConstructorConstraint)
                Context.Emit(Tuples.general_type_parameter_constraints(constraints, 3));

            ITypeSymbol baseType = symbol.HasValueTypeConstraint ?
                    Context.Compilation.GetTypeByMetadataName(valueTypeName) :
                    Context.Compilation.ObjectType;

            var constraintTypes = new List<Type>();
            foreach (var abase in symbol.ConstraintTypes)
            {
                if (abase.TypeKind != TypeKind.Interface)
                    baseType = abase;
                var t = Create(Context, abase);
                Context.Emit(Tuples.specific_type_parameter_constraints(constraints, t.TypeRef));
                constraintTypes.Add(t);
            }

            Context.Emit(Tuples.types(this, Semmle.Extraction.Kinds.TypeKind.TYPE_PARAMETER, symbol.Name));
            Context.Emit(Tuples.extend(this, Create(Context, baseType).TypeRef));

            Namespace parentNs = Namespace.Create(Context, symbol.TypeParameterKind == TypeParameterKind.Method ? Context.Compilation.GlobalNamespace : symbol.ContainingNamespace);
            Context.Emit(Tuples.parent_namespace(this, parentNs));

            foreach (var l in symbol.Locations)
            {
                Context.Emit(Tuples.type_location(this, Context.Create(l)));
            }

            if (this.IsSourceDeclaration)
            {
                var declSyntaxReferences = symbol.DeclaringSyntaxReferences.Select(d => d.GetSyntax()).
                    Select(s => s.Parent).Where(p => p != null).Select(p => p.Parent).ToArray();
                var clauses = declSyntaxReferences.OfType<MethodDeclarationSyntax>().SelectMany(m => m.ConstraintClauses);
                clauses = clauses.Concat(declSyntaxReferences.OfType<ClassDeclarationSyntax>().SelectMany(c => c.ConstraintClauses));
                clauses = clauses.Concat(declSyntaxReferences.OfType<InterfaceDeclarationSyntax>().SelectMany(c => c.ConstraintClauses));
                clauses = clauses.Concat(declSyntaxReferences.OfType<StructDeclarationSyntax>().SelectMany(c => c.ConstraintClauses));
                int i = 0;
                foreach (var clause in clauses.Where(c => c.Name.ToString() == symbol.Name))
                {
                    TypeMention.Create(Context, clause.Name, this, this);
                    foreach (var constraint in clause.Constraints.OfType<TypeConstraintSyntax>())
                        TypeMention.Create(Context, constraint.Type, this, constraintTypes[i++]);
                }
            }
        }

        static public TypeParameter Create(Context cx, ITypeParameterSymbol p) =>
            TypeParameterFactory.Instance.CreateEntity(cx, p);

        /// <summary>
        /// The variance of this type parameter.
        /// </summary>
        public Variance Variance
        {
            get
            {
                switch (symbol.Variance)
                {
                    case VarianceKind.None: return Variance.None;
                    case VarianceKind.Out: return Variance.Out;
                    case VarianceKind.In: return Variance.In;
                    default:
                        throw new InternalError("Unexpected VarianceKind {0}", symbol.Variance);
                }
            }
        }

        public override IId Id
        {
            get
            {
                string kind;
                IEntity containingEntity;
                switch (symbol.TypeParameterKind)
                {
                    case TypeParameterKind.Method:
                        kind = "methodtypeparameter";
                        containingEntity = Method.Create(Context, (IMethodSymbol)symbol.ContainingSymbol);
                        break;
                    case TypeParameterKind.Type:
                        kind = "typeparameter";
                        containingEntity = Create(Context, symbol.ContainingType);
                        break;
                    default:
                        throw new InternalError(symbol, "Unhandled type parameter kind {0}", symbol.TypeParameterKind);
                }
                return new Key(containingEntity, "_", symbol.Ordinal, ";", kind);
            }
        }

        class TypeParameterFactory : ICachedEntityFactory<ITypeParameterSymbol, TypeParameter>
        {
            public static readonly TypeParameterFactory Instance = new TypeParameterFactory();

            public TypeParameter Create(Context cx, ITypeParameterSymbol init) => new TypeParameter(cx, init);
        }
    }
}
