using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

// ─────────────────────────────────────────────────────────────────────────────
// What the mod actually calls, versus what a given game version actually has.
//
// The mod ships ONE DLL, compiled against the newest game, to every supported
// version. Anything it calls that a older version declares differently is a
// MissingMethodException at JIT time — on the frame thread, inside a feature, with
// nothing at the call site looking version-specific.
//
// Four of those got through hand-surveys of the decompiled source in a row, each
// through the gap the previous method left: checking the name existed, then the
// declaring type, then the signature, then a type I had not thought to check at all.
// Every one of those asks "did I remember to look here". This reads the DLL's own
// member references, so the question becomes what it calls, which is not a matter
// of opinion.
//
// Reads metadata only — no assembly is loaded or executed, so it does not care that
// these are Unity assemblies built for another runtime.
// ─────────────────────────────────────────────────────────────────────────────

internal static class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: CmmApiDiff <mod.dll> <target-Assembly-CSharp.dll> [more targets...]");
            return 2;
        }

        string modPath = args[0];
        if (!File.Exists(modPath)) { Console.WriteLine("no such file: " + modPath); return 2; }

        List<Ref> refs = ReadReferences(modPath);
        Console.WriteLine($"{Path.GetFileName(modPath)} references {refs.Count} member(s) from other assemblies.");

        int failures = 0;
        for (int i = 1; i < args.Length; i++)
        {
            string target = args[i];
            if (!File.Exists(target)) { Console.WriteLine($"\n-- {target}: NOT FOUND, skipped"); continue; }

            HashSet<string> have = ReadDeclared(target);
            var missing = new List<Ref>();
            foreach (Ref r in refs)
            {
                // Only members from the GAME's own assembly can differ per version; BCL and
                // Unity references are pinned by the build and are not what this is looking for.
                if (!LooksLikeGameScope(r.Scope)) continue;
                if (!have.Contains(r.Key)) missing.Add(r);
            }

            string name = Path.GetFileName(Path.GetDirectoryName(target)) + "/" + Path.GetFileName(target);
            if (missing.Count == 0)
            {
                Console.WriteLine($"\n-- {name}: ok, every referenced member exists");
                continue;
            }

            failures++;
            Console.WriteLine($"\n-- {name}: {missing.Count} MISSING");
            foreach (Ref r in missing.OrderBy(m => m.Type).ThenBy(m => m.Name))
                Console.WriteLine($"     {r.Type}.{r.Name}({r.Params})");
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "PASS - the mod calls nothing these versions do not have."
            : $"FAIL - {failures} version(s) are missing something the mod calls.");
        return failures == 0 ? 0 : 1;
    }

    sealed class Ref
    {
        public string Scope, Type, Name, Params;
        public string Key => $"{Type}.{Name}({Params})";
    }

    // A member reference the mod emits: the type it is called on, the name, and the
    // parameter types as the signature blob spells them.
    static List<Ref> ReadReferences(string path)
    {
        var outp = new List<Ref>();
        using var fs = File.OpenRead(path);
        using var pe = new PEReader(fs);
        MetadataReader md = pe.GetMetadataReader();
        var dec = new SigDecoder();

        foreach (MemberReferenceHandle h in md.MemberReferences)
        {
            MemberReference mr = md.GetMemberReference(h);
            if (mr.GetKind() != MemberReferenceKind.Method) continue;

            string type = null, scope = null;
            if (mr.Parent.Kind == HandleKind.TypeReference)
            {
                TypeReference tr = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
                string ns = md.GetString(tr.Namespace);
                type = (ns.Length > 0 ? ns + "." : "") + md.GetString(tr.Name);
                if (tr.ResolutionScope.Kind == HandleKind.AssemblyReference)
                    scope = md.GetString(md.GetAssemblyReference(
                        (AssemblyReferenceHandle)tr.ResolutionScope).Name);
            }
            if (type == null) continue;

            MethodSignature<string> sig;
            try { sig = mr.DecodeMethodSignature(dec, null); }
            catch { continue; }

            outp.Add(new Ref
            {
                Scope = scope ?? "?",
                Type = type,
                Name = md.GetString(mr.Name),
                Params = string.Join(",", sig.ParameterTypes),
            });
        }
        return outp;
    }

    // Every method a target assembly declares, in the same shape, so the two can be compared
    // as strings. Includes inherited-from-nothing only: a member the mod calls on a base type
    // must be declared on that base type, which is exactly the GetChunkSync case -- present on
    // the derived type in the older version and therefore NOT a match.
    static HashSet<string> ReadDeclared(string path)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var fs = File.OpenRead(path);
        using var pe = new PEReader(fs);
        MetadataReader md = pe.GetMetadataReader();
        var dec = new SigDecoder();

        foreach (TypeDefinitionHandle th in md.TypeDefinitions)
        {
            TypeDefinition td = md.GetTypeDefinition(th);
            string ns = md.GetString(td.Namespace);
            string type = (ns.Length > 0 ? ns + "." : "") + md.GetString(td.Name);

            foreach (MethodDefinitionHandle mh in td.GetMethods())
            {
                MethodDefinition mdf = md.GetMethodDefinition(mh);
                MethodSignature<string> sig;
                try { sig = mdf.DecodeSignature(dec, null); }
                catch { continue; }
                set.Add($"{type}.{md.GetString(mdf.Name)}({string.Join(",", sig.ParameterTypes)})");
            }
        }
        return set;
    }

    // The game's own code, as opposed to the BCL or Unity. Assembly-CSharp is where every
    // version-specific game type lives.
    static bool LooksLikeGameScope(string scope)
        => scope != null && scope.StartsWith("Assembly-CSharp", StringComparison.OrdinalIgnoreCase);

    // Signature blobs only need to come out as readable, STABLE strings -- both sides are
    // decoded the same way, so the exact spelling does not matter as long as it is consistent.
    sealed class SigDecoder : ISignatureTypeProvider<string, object>
    {
        public string GetArrayType(string e, ArrayShape s) => e + "[]";
        public string GetByReferenceType(string e) => e + "&";
        public string GetPointerType(string e) => e + "*";
        public string GetGenericInstantiation(string g, System.Collections.Immutable.ImmutableArray<string> a)
            => g + "<" + string.Join(",", a) + ">";
        public string GetGenericMethodParameter(object _, int i) => "!!" + i;
        public string GetGenericTypeParameter(object _, int i) => "!" + i;
        public string GetModifiedType(string mod, string un, bool req) => un;
        public string GetPinnedType(string e) => e;
        public string GetPrimitiveType(PrimitiveTypeCode c) => c.ToString();
        public string GetSZArrayType(string e) => e + "[]";
        public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
        public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte _)
        {
            TypeDefinition td = r.GetTypeDefinition(h);
            string ns = r.GetString(td.Namespace);
            return (ns.Length > 0 ? ns + "." : "") + r.GetString(td.Name);
        }
        public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte _)
        {
            TypeReference tr = r.GetTypeReference(h);
            string ns = r.GetString(tr.Namespace);
            return (ns.Length > 0 ? ns + "." : "") + r.GetString(tr.Name);
        }
        public string GetTypeFromSpecification(MetadataReader r, object _, TypeSpecificationHandle h, byte b)
            => r.GetTypeSpecification(h).DecodeSignature(this, null);
    }
}
