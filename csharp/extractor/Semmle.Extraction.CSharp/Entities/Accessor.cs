using Microsoft.CodeAnalysis;
using System.Linq;

namespace Semmle.Extraction.CSharp.Entities
{
    class Accessor : Method
    {
        protected Accessor(Context cx, IMethodSymbol init)
            : base(cx, init) { }

        /// <summary>
        /// Gets the property symbol associated with this accessor.
        /// </summary>
        IPropertySymbol PropertySymbol
        {
            get
            {
                // Usually, the property/indexer can be fetched from the associated symbol
                var prop = symbol.AssociatedSymbol as IPropertySymbol;
                if (prop != null)
                    return prop;

                // But for properties/indexers that implement explicit interfaces, Roslyn
                // does not properly populate `AssociatedSymbol`
                var props = symbol.ContainingType.GetMembers().OfType<IPropertySymbol>();
                props = props.Where(p => symbol.Equals(p.GetMethod) || symbol.Equals(p.SetMethod));
                return props.SingleOrDefault();
            }
        }

        public new Accessor OriginalDefinition => Create(Context, symbol.OriginalDefinition);

        public override void Populate()
        {
            PopulateMethod();
            ExtractModifiers();
            ContainingType.ExtractGenerics();

            var prop = PropertySymbol;
            if (prop == null)
            {
                Context.ModelError(symbol, "Unhandled accessor associated symbol");
                return;
            }

            var parent = Property.Create(Context, prop);
            int kind;
            Accessor unboundAccessor;
            if (symbol.Equals(prop.GetMethod))
            {
                kind = 1;
                unboundAccessor = Create(Context, prop.OriginalDefinition.GetMethod);
            }
            else if (symbol.Equals(prop.SetMethod))
            {
                kind = 2;
                unboundAccessor = Create(Context, prop.OriginalDefinition.SetMethod);
            }
            else
            {
                Context.ModelError(symbol, "Undhandled accessor kind");
                return;
            }

            Context.Emit(Tuples.accessors(this, kind, symbol.Name, parent, unboundAccessor));

            foreach (var l in Locations)
                Context.Emit(Tuples.accessor_location(this, l));

            Overrides();

            if (symbol.FromSource() && Block == null)
            {
                Context.Emit(Tuples.compiler_generated(this));
            }
        }

        public new static Accessor Create(Context cx, IMethodSymbol symbol) =>
            AccessorFactory.Instance.CreateEntity(cx, symbol);

        class AccessorFactory : ICachedEntityFactory<IMethodSymbol, Accessor>
        {
            public static readonly AccessorFactory Instance = new AccessorFactory();

            public Accessor Create(Context cx, IMethodSymbol init) => new Accessor(cx, init);
        }
    }
}
