using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using Xunit.Abstractions;

namespace Tests;

// After a game update, run `dotnet test tests` BEFORE launching: every game member the mod names in
// a string — Harmony patch targets, private fields — is looked up in the installed game's assemblies.
// A rename fails here with the file and line, instead of a screen going silent in-game.
//
// Covered call shapes (owner type taken from the code's own static types):
//   AccessTools.Field / Method / DeclaredMethod / PropertyGetter / PropertySetter (typeof(T), "name", ...)
//   ReadField<X>(expr, "name")                   owner = static type of expr
//   typeof(T).GetField / GetMethod / GetProperty("name", ...)
//   expr.GetType().GetField / GetMethod / GetProperty("name", ...)   owner = static type of expr
// When the owner is object / an interface / unresolved, the name can't be checked offline; those are
// counted and printed, and stay covered at runtime by Compat.
public class GameCompatTests
{
    private readonly ITestOutputHelper _out;
    public GameCompatTests(ITestOutputHelper output) => _out = output;

    private enum Kind { Field, Method, Property, Any }

    [Fact]
    public void Every_named_game_member_exists_in_the_installed_game()
    {
        var comp = ModSources.Compilation();
        if (comp == null)
        {
            _out.WriteLine($"SKIPPED: game not found at '{ModSources.GameDir()}' (set PTCGL_GAME_DIR)");
            return;
        }

        var missing = new List<string>();
        var unverifiable = new List<string>();
        var verified = 0;

        foreach (var tree in comp.SyntaxTrees)
        {
            var model = comp.GetSemanticModel(tree);
            foreach (var call in tree.GetRoot().DescendantNodes().Where(n => n is InvocationExpressionSyntax or AttributeSyntax))
            {
                var found = call is AttributeSyntax attr
                    ? TryGetPatchTarget(attr, model, out var owner, out var name, out var kind)
                    : TryGetLookup((InvocationExpressionSyntax)call, model, out owner, out name, out kind);
                if (!found) continue;
                var where = $"{tree.FilePath}:{call.GetLocation().GetLineSpan().StartLinePosition.Line + 1}";
                if (owner == null || owner.SpecialType == SpecialType.System_Object || owner.TypeKind is TypeKind.Error or TypeKind.TypeParameter)
                {
                    unverifiable.Add($"{where}  \"{name}\"");
                    continue;
                }
                if (IsModType(owner, comp)) continue;
                verified++;
                if (HasMember(owner, name, kind)) continue;
                // A variable typed as a Unity base (Component, MonoBehaviour…) holds some game subclass
                // only known at runtime — the field lives there, not on the static type.
                if (owner.ContainingAssembly?.Name?.StartsWith("UnityEngine") == true) { verified--; unverifiable.Add($"{where}  {owner.Name}.{name} (runtime subclass)"); continue; }
                missing.Add($"{where}  {owner.ToDisplayString()}.{name} ({kind})");
            }
        }

        _out.WriteLine($"verified {verified} game member names; {unverifiable.Count} not checkable offline");
        foreach (var u in unverifiable) _out.WriteLine("  unverifiable: " + u);
        Assert.True(verified > 0, "no game member lookups recognised — has the reflection style changed?");
        Assert.True(missing.Count == 0, "Game members the mod names but this game build doesn't have:\n" + string.Join("\n", missing));
    }

    // [HarmonyPatch(typeof(T), "name", ...)], optionally with MethodType.Getter / Setter for a property.
    private static bool TryGetPatchTarget(AttributeSyntax attr, SemanticModel model,
        out ITypeSymbol? owner, out string name, out Kind kind)
    {
        owner = null; name = ""; kind = Kind.Method;
        if (attr.Name.ToString() is not ("HarmonyPatch" or "HarmonyLib.HarmonyPatch")) return false;
        var args = attr.ArgumentList?.Arguments;
        if (args is not { Count: >= 2 } || args.Value[0].Expression is not TypeOfExpressionSyntax t) return false;
        if (args.Value[1].Expression is not LiteralExpressionSyntax l || !l.IsKind(SyntaxKind.StringLiteralExpression)) return false;
        name = l.Token.ValueText;
        if (args.Value.Any(a => a.ToString() is "MethodType.Getter" or "MethodType.Setter")) kind = Kind.Property;
        owner = model.GetTypeInfo(t.Type).Type;
        return true;
    }

    private static bool TryGetLookup(InvocationExpressionSyntax call, SemanticModel model,
        out ITypeSymbol? owner, out string name, out Kind kind)
    {
        owner = null; name = ""; kind = Kind.Any;
        var args = call.ArgumentList.Arguments;
        string? method = call.Expression switch
        {
            MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
            GenericNameSyntax g => g.Identifier.Text,
            IdentifierNameSyntax i => i.Identifier.Text,
            _ => null,
        };
        if (method == null) return false;

        // AccessTools.X(typeof(T), "name", ...)
        if (call.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "AccessTools" } })
        {
            kind = method switch
            {
                "Field" or "DeclaredField" => Kind.Field,
                "Method" or "DeclaredMethod" => Kind.Method,
                "Property" or "DeclaredProperty" or "PropertyGetter" or "PropertySetter" => Kind.Property,
                _ => (Kind)(-1),
            };
            if ((int)kind < 0 || args.Count < 2 || args[0].Expression is not TypeOfExpressionSyntax t) return false;
            if (!Literal(args[1], out name)) return false;
            owner = model.GetTypeInfo(t.Type).Type;
            return true;
        }

        // ReadField<X>(expr, "name")
        if (method == "ReadField" && args.Count == 2 && Literal(args[1], out name))
        {
            kind = Kind.Field;
            owner = model.GetTypeInfo(args[0].Expression).Type;
            return true;
        }

        // typeof(T).GetField("name") / expr.GetType().GetField("name")
        if (call.Expression is MemberAccessExpressionSyntax ma && method is "GetField" or "GetMethod" or "GetProperty"
            && args.Count >= 1 && Literal(args[0], out name))
        {
            kind = method switch { "GetField" => Kind.Field, "GetMethod" => Kind.Method, _ => Kind.Property };
            if (ma.Expression is TypeOfExpressionSyntax tt) { owner = model.GetTypeInfo(tt.Type).Type; return true; }
            if (ma.Expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "GetType" } inner, ArgumentList.Arguments.Count: 0 })
            {
                owner = model.GetTypeInfo(inner.Expression).Type;
                return true;
            }
        }
        return false;
    }

    private static bool Literal(ArgumentSyntax a, out string value)
    {
        value = "";
        if (a.Expression is LiteralExpressionSyntax l && l.IsKind(SyntaxKind.StringLiteralExpression)) { value = l.Token.ValueText; return true; }
        if (a.Expression is InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "nameof" } }) return false; // compiler-checked
        return false;
    }

    private static bool IsModType(ITypeSymbol t, Compilation comp) =>
        SymbolEqualityComparer.Default.Equals(t.ContainingAssembly, comp.Assembly);

    private static bool HasMember(ITypeSymbol owner, string name, Kind kind)
    {
        for (var t = owner; t != null; t = t.BaseType)
        {
            foreach (var m in t.GetMembers(name))
            {
                if (kind == Kind.Any) return true;
                if (kind == Kind.Field && m is IFieldSymbol) return true;
                if (kind == Kind.Property && m is IPropertySymbol) return true;
                if (kind == Kind.Method && m is IMethodSymbol) return true;
            }
            // Property accessors looked up as methods (get_X / set_X).
            if (kind == Kind.Method && (name.StartsWith("get_") || name.StartsWith("set_"))
                && t.GetMembers(name.Substring(4)).OfType<IPropertySymbol>().Any()) return true;
        }
        return false;
    }
}
