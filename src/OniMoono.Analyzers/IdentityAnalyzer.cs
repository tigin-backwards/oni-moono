using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace OniMoono.Analyzers
{
    /// Keeps the engine's comparisons of its objects by reference where reference identity is
    /// meant (docs/research/facade-merge.md, category 2).
    ///
    /// Two kinds of type are watched:
    ///
    /// - **Engine records** of game objects and components: OniMoono.Engine.GameObjectNative,
    ///   ComponentNative and the types derived from them, and NativeObject itself, which holds
    ///   them too. They compare by reference today. Merged into a facade they would compare with
    ///   Unity's fake-null equality, so every comparison of one is written in a form that keeps
    ///   its meaning either way: `is null`, `is not null`, ReferenceEquals, a reference comparer.
    ///   These rules apply wherever a record is visible. The records of assets (textures,
    ///   meshes, shaders…) stay records and are not checked.
    /// - **Facades**: UnityEngine.Object and the types derived from it, whose `==`, `!=` and
    ///   Equals are Unity's (a destroyed object equals null and every other destroyed object). In
    ///   the engine's own code those are written out: `is null` or ReferenceEquals for identity,
    ///   the bool conversion (`!obj`, `if (obj)`) for liveness. The engine's own code is the
    ///   OniMoono namespaces of the engine and its desktop platform, and in the engine the
    ///   facades' members the game cannot call. The facades' public members, Unity's packages and
    ///   the host (whose UI and game tweaks are game code) use Unity's operators as Unity's code
    ///   does, and are not checked for them.
    ///
    /// A site that means Unity's equality can say so with `#pragma warning disable` and a reason.
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class IdentityAnalyzer : DiagnosticAnalyzer
    {
        const string Category = "Identity";

        public static readonly DiagnosticDescriptor RecordEquality = new(
            "MOONO001", "Equality operator on an engine record",
            "'{0}' on the engine record type '{1}': write 'is null', 'is not null' or ReferenceEquals, which keep their meaning if the record merges into its facade",
            Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor FacadeEquality = new(
            "MOONO002", "Unity's equality operator in engine code",
            "'{0}' on '{1}' is Unity's fake-null comparison: write 'is null'/'is not null'/ReferenceEquals for identity, or the bool conversion for liveness",
            Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor EqualsCall = new(
            "MOONO003", "Equals on an engine record or facade",
            "Equals on '{0}': use ReferenceEquals for identity (a facade's Equals is Unity's fake-null equality)",
            Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor DefaultComparer = new(
            "MOONO004", "Default equality comparer for engine records or facades",
            "{0} compares '{1}' with the default comparer: pass ReferenceEqualityComparer.Instance",
            Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor EqualitySearch = new(
            "MOONO005", "Search by default equality for engine records or facades",
            "'{0}' finds '{1}' by Equals: search by reference (OniMoono.Engine.ByReference)",
            Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(RecordEquality, FacadeEquality, EqualsCall, DefaultComparer, EqualitySearch);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(start =>
            {
                var types = new Types(start.Compilation);
                if (types.Record == null && types.Facade == null) return;
                start.RegisterOperationAction(c => Binary(c, types), OperationKind.Binary);
                start.RegisterOperationAction(c => TupleBinary(c, types), OperationKind.TupleBinary);
                start.RegisterOperationAction(c => Invocation(c, types), OperationKind.Invocation);
                start.RegisterOperationAction(c => Creation(c, types), OperationKind.ObjectCreation);
                start.RegisterOperationAction(c => CollectionExpression(c, types), OperationKind.CollectionExpression);
                start.RegisterOperationAction(c => PropertyReference(c, types), OperationKind.PropertyReference);
            });
        }

        enum Kind { None, Record, Facade }

        sealed class Types
        {
            public readonly INamedTypeSymbol Record, GameObjectRecord, ComponentRecord, Facade, EqualityComparerOfT, IEqualityComparerOfT;

            public Types(Compilation c)
            {
                Record = c.GetTypeByMetadataName("OniMoono.Engine.NativeObject");
                GameObjectRecord = c.GetTypeByMetadataName("OniMoono.Engine.GameObjectNative");
                ComponentRecord = c.GetTypeByMetadataName("OniMoono.Engine.ComponentNative");
                Facade = c.GetTypeByMetadataName("UnityEngine.Object");
                EqualityComparerOfT = c.GetTypeByMetadataName("System.Collections.Generic.EqualityComparer`1");
                IEqualityComparerOfT = c.GetTypeByMetadataName("System.Collections.Generic.IEqualityComparer`1");
            }

            public Kind KindOf(ITypeSymbol t)
            {
                if (t is ITypeParameterSymbol p)
                {
                    foreach (var c in p.ConstraintTypes)
                        if (KindOf(c) is var k && k != Kind.None) return k;
                    return Kind.None;
                }
                if (Record != null && SymbolEqualityComparer.Default.Equals(t, Record)) return Kind.Record;
                for (var b = t as INamedTypeSymbol; b != null; b = b.BaseType)
                {
                    if (SymbolEqualityComparer.Default.Equals(b, GameObjectRecord) || SymbolEqualityComparer.Default.Equals(b, ComponentRecord)) return Kind.Record;
                    if (Facade != null && SymbolEqualityComparer.Default.Equals(b, Facade)) return Kind.Facade;
                }
                return Kind.None;
            }

            /// The kind a key of this type compares as: its own, or for a tuple or a C# record the
            /// first of its elements' or fields' that is watched (both compare them by the default
            /// comparer).
            public Kind KeyKind(ITypeSymbol t, out ITypeSymbol watched) => KeyKind(t, out watched, 0);

            Kind KeyKind(ITypeSymbol t, out ITypeSymbol watched, int depth)
            {
                watched = t;
                var k = KindOf(t);
                if (k != Kind.None || depth > 4) return k;
                if (t is INamedTypeSymbol { IsTupleType: true } tuple)
                {
                    foreach (var e in tuple.TupleElements)
                        if (KeyKind(e.Type, out watched, depth + 1) is var ek && ek != Kind.None) return ek;
                }
                else if (t is INamedTypeSymbol { IsRecord: true } record)
                {
                    foreach (var m in record.GetMembers())
                        if (m is IFieldSymbol { IsStatic: false } f && KeyKind(f.Type, out watched, depth + 1) is var fk && fk != Kind.None) return fk;
                }
                watched = t;
                return Kind.None;
            }
        }

        /// Facades are checked only in the engine's own code: in the engine and its desktop
        /// platform, the OniMoono namespaces, and in the engine the members of the facades that the
        /// game cannot call (internal or private, or in such a type). The host's own UI and game
        /// tweaks work on game objects as game code does, with Unity's operators.
        static bool EngineCode(ISymbol s)
        {
            if (s?.ContainingAssembly?.Name is not ("OniMoono.UnityEngine" or "OniMoono.Desktop")) return false;
            for (var n = s?.ContainingNamespace; n != null && !n.IsGlobalNamespace; n = n.ContainingNamespace)
                if (n.ContainingNamespace is { IsGlobalNamespace: true } && n.Name == "OniMoono") return true;
            if (s?.ContainingAssembly?.Name != "OniMoono.UnityEngine") return false;
            while (s is IMethodSymbol { MethodKind: MethodKind.LambdaMethod or MethodKind.LocalFunction }) s = s.ContainingSymbol;
            if (s is IMethodSymbol { AssociatedSymbol: { } property }) s = property;
            for (var x = s; x != null && x is not INamespaceSymbol; x = x.ContainingSymbol)
                if (x.DeclaredAccessibility is Accessibility.Private or Accessibility.Internal or Accessibility.ProtectedAndInternal) return true;
            return false;
        }

        static bool Watched(Kind k, ISymbol container) => k == Kind.Record || (k == Kind.Facade && EngineCode(container));

        static ITypeSymbol Unconverted(IOperation op)
        {
            while (op is IConversionOperation { IsImplicit: true } c) op = c.Operand;
            return op.Type;
        }

        static void Binary(OperationAnalysisContext c, Types types)
        {
            var op = (IBinaryOperation)c.Operation;
            if (op.OperatorKind != BinaryOperatorKind.Equals && op.OperatorKind != BinaryOperatorKind.NotEquals) return;
            string text = op.OperatorKind == BinaryOperatorKind.Equals ? "==" : "!=";
            if (op.OperatorMethod is { ContainingType.IsRecord: true, IsImplicitlyDeclared: true } synthesized)
            {
                // A C# record's == compares its fields by the default comparer.
                if (types.KeyKind(synthesized.ContainingType, out var field) is var k && Watched(k, c.ContainingSymbol))
                    c.ReportDiagnostic(Diagnostic.Create(k == Kind.Record ? RecordEquality : FacadeEquality, op.Syntax.GetLocation(),
                        $"{text} of {synthesized.ContainingType.Name}", field.ToDisplayString()));
                return;
            }
            if (op.OperatorMethod != null)
            {
                if (types.Facade != null && SymbolEqualityComparer.Default.Equals(op.OperatorMethod.ContainingType, types.Facade)
                    && EngineCode(c.ContainingSymbol))
                {
                    var t = Unconverted(op.LeftOperand) is { } l && types.KindOf(l) == Kind.Facade ? l : Unconverted(op.RightOperand);
                    c.ReportDiagnostic(Diagnostic.Create(FacadeEquality, op.Syntax.GetLocation(), text, t?.ToDisplayString() ?? "UnityEngine.Object"));
                }
                return;
            }
            foreach (var operand in new[] { op.LeftOperand, op.RightOperand })
                if (Unconverted(operand) is { } t && types.KindOf(t) == Kind.Record)
                {
                    c.ReportDiagnostic(Diagnostic.Create(RecordEquality, op.Syntax.GetLocation(), text, t.ToDisplayString()));
                    return;
                }
        }

        static void TupleBinary(OperationAnalysisContext c, Types types)
        {
            var op = (ITupleBinaryOperation)c.Operation;
            foreach (var operand in new[] { op.LeftOperand, op.RightOperand })
                if (TupleKind(operand, types, out var watched) is var k && Watched(k, c.ContainingSymbol))
                {
                    var d = k == Kind.Record ? RecordEquality : FacadeEquality;
                    c.ReportDiagnostic(Diagnostic.Create(d, op.Syntax.GetLocation(), "tuple ==", watched.ToDisplayString()));
                    return;
                }
        }

        /// A tuple's elements compare with their own ==: a tuple literal's elements one by one.
        static Kind TupleKind(IOperation operand, Types types, out ITypeSymbol watched)
        {
            while (operand is IConversionOperation { IsImplicit: true } conversion) operand = conversion.Operand;
            if (operand is ITupleOperation tuple)
            {
                foreach (var e in tuple.Elements)
                    if (TupleKind(e, types, out watched) is var ek && ek != Kind.None) return ek;
                watched = null;
                return Kind.None;
            }
            watched = operand.Type;
            return operand.Type == null ? Kind.None : types.KeyKind(operand.Type, out watched);
        }

        static readonly ImmutableHashSet<string> SearchCollections = ImmutableHashSet.Create(
            "List", "IList", "ICollection", "Collection", "ReadOnlyCollection", "LinkedList", "Queue", "Stack", "IReadOnlyList", "SortedList");

        static readonly ImmutableHashSet<string> SearchMethods = ImmutableHashSet.Create("Contains", "IndexOf", "LastIndexOf", "Remove");

        static void Invocation(OperationAnalysisContext c, Types types)
        {
            var op = (IInvocationOperation)c.Operation;
            var m = op.TargetMethod;
            var container = c.ContainingSymbol;

            if (m.Name == "Equals")
            {
                ITypeSymbol subject = null;
                if (!m.IsStatic && m.Parameters.Length == 1 && op.Instance != null) subject = Unconverted(op.Instance);
                else if (m.IsStatic && m.Parameters.Length == 2 && m.ContainingType.SpecialType == SpecialType.System_Object)
                    subject = new[] { Unconverted(op.Arguments[0].Value), Unconverted(op.Arguments[1].Value) }
                        .FirstOrDefault(t => t != null && types.KindOf(t) != Kind.None);
                if (subject != null && types.KeyKind(subject, out var watchedSubject) is var sk && Watched(sk, container))
                    c.ReportDiagnostic(Diagnostic.Create(EqualsCall, op.Syntax.GetLocation(), watchedSubject.ToDisplayString()));
                return;
            }

            var owner = m.ContainingType?.OriginalDefinition;
            if (owner == null) return;
            string ns = owner.ContainingNamespace?.ToDisplayString();

            // List<T>.Contains/IndexOf/LastIndexOf/Remove and the like.
            if (ns is "System.Collections.Generic" or "System.Collections.ObjectModel" && SearchCollections.Contains(owner.Name)
                && SearchMethods.Contains(m.Name) && m.ContainingType.TypeArguments.Length == 1
                && !m.Parameters.Any(IsComparer)
                && types.KeyKind(m.ContainingType.TypeArguments[0], out var element) is var ek && Watched(ek, container))
            {
                c.ReportDiagnostic(Diagnostic.Create(EqualitySearch, op.Syntax.GetLocation(), $"{owner.Name}.{m.Name}", element.ToDisplayString()));
                return;
            }

            // Array.IndexOf/LastIndexOf, generic or not.
            if (ns == "System" && owner.Name == "Array" && m.Name is "IndexOf" or "LastIndexOf" && op.Arguments.Length > 0
                && Unconverted(op.Arguments[0].Value) is IArrayTypeSymbol array && types.KeyKind(array.ElementType, out var item) is var ak && Watched(ak, container))
            {
                c.ReportDiagnostic(Diagnostic.Create(EqualitySearch, op.Syntax.GetLocation(), $"Array.{m.Name}", item.ToDisplayString()));
                return;
            }

            // LINQ's set operators and lookups without a comparer.
            if (ns == "System.Linq" && owner.Name == "Enumerable" && !m.Parameters.Any(IsComparer))
            {
                ITypeSymbol key = m.Name switch
                {
                    "Contains" or "Distinct" or "Except" or "Intersect" or "Union" or "ToHashSet" or "SequenceEqual"
                        => m.TypeArguments.Length > 0 ? m.TypeArguments[0] : null,
                    "ToDictionary" or "ToLookup" or "GroupBy" or "Join" or "GroupJoin" or "DistinctBy" or "ExceptBy" or "IntersectBy" or "UnionBy" or "CountBy" or "AggregateBy"
                        => KeyTypeArgument(m),
                    _ => null,
                };
                if (key != null && types.KeyKind(key, out var watched) is var k && Watched(k, container))
                    c.ReportDiagnostic(Diagnostic.Create(m.Name is "Contains" or "SequenceEqual" ? EqualitySearch : DefaultComparer,
                        op.Syntax.GetLocation(), $"Enumerable.{m.Name}", watched.ToDisplayString()));
            }
        }

        static ITypeSymbol KeyTypeArgument(IMethodSymbol m)
        {
            var def = m.OriginalDefinition;
            for (int i = 0; i < def.TypeParameters.Length; i++)
                if (def.TypeParameters[i].Name == "TKey") return m.TypeArguments[i];
            return null;
        }

        static bool IsComparer(IParameterSymbol p) => p.Type.OriginalDefinition is INamedTypeSymbol { Name: "IEqualityComparer" or "IComparer" };

        static readonly ImmutableHashSet<string> HashedCollections = ImmutableHashSet.Create(
            "HashSet", "Dictionary", "ConcurrentDictionary", "ImmutableHashSet", "ImmutableDictionary", "OrderedDictionary", "FrozenSet", "FrozenDictionary");

        static void Creation(OperationAnalysisContext c, Types types)
        {
            var op = (IObjectCreationOperation)c.Operation;
            if (op.Type is not INamedTypeSymbol { IsGenericType: true } type || !HashedCollections.Contains(type.Name)) return;
            if (op.Constructor != null && op.Constructor.Parameters.Any(IsComparer)) return;
            var key = type.TypeArguments[0];
            if (types.KeyKind(key, out var watched) is var k && Watched(k, c.ContainingSymbol))
                c.ReportDiagnostic(Diagnostic.Create(DefaultComparer, op.Syntax.GetLocation(), $"new {type.Name}", watched.ToDisplayString()));
        }

        static void CollectionExpression(OperationAnalysisContext c, Types types)
        {
            var op = c.Operation;
            if (op.Type is not INamedTypeSymbol { IsGenericType: true } type || !HashedCollections.Contains(type.Name)) return;
            if (types.KeyKind(type.TypeArguments[0], out var watched) is var k && Watched(k, c.ContainingSymbol))
                c.ReportDiagnostic(Diagnostic.Create(DefaultComparer, op.Syntax.GetLocation(), $"a {type.Name} collection expression", watched.ToDisplayString()));
        }

        static void PropertyReference(OperationAnalysisContext c, Types types)
        {
            var op = (IPropertyReferenceOperation)c.Operation;
            if (op.Property.Name != "Default" || types.EqualityComparerOfT == null) return;
            var owner = op.Property.ContainingType;
            if (!SymbolEqualityComparer.Default.Equals(owner.OriginalDefinition, types.EqualityComparerOfT)) return;
            if (types.KeyKind(owner.TypeArguments[0], out var watched) is var k && Watched(k, c.ContainingSymbol))
                c.ReportDiagnostic(Diagnostic.Create(DefaultComparer, op.Syntax.GetLocation(), "EqualityComparer.Default", watched.ToDisplayString()));
        }
    }
}
