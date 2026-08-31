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

        // KNOWN-AND-GATED, so the tool can be green and therefore worth running. See the file.
        Dictionary<string, string> allowed = ReadAllowList(out string allowPath);
        if (allowed.Count > 0)
            Console.WriteLine($"{allowed.Count} member(s) allow-listed as gated ({Path.GetFileName(allowPath)}).");

        int failures = 0;
        for (int i = 1; i < args.Length; i++)
        {
            string target = args[i];
            if (!File.Exists(target)) { Console.WriteLine($"\n-- {target}: NOT FOUND, skipped"); continue; }

            HashSet<string> have = ReadDeclared(target);
            var missing = new List<Ref>();
            var gated = new List<Ref>();
            foreach (Ref r in refs)
            {
                // Only members from the GAME's own assembly can differ per version; BCL and
                // Unity references are pinned by the build and are not what this is looking for.
                if (!LooksLikeGameScope(r.Scope)) continue;
                if (have.Contains(r.Key)) continue;
                if (allowed.ContainsKey(Display(r))) gated.Add(r); else missing.Add(r);
            }

            string name = Path.GetFileName(Path.GetDirectoryName(target)) + "/" + Path.GetFileName(target);
            // Reported either way. A gated member is not a problem, but it IS the thing that
            // becomes one the day somebody calls it from outside the gate, so it stays visible.
            string gatedNote = gated.Count == 0 ? "" : $"  ({gated.Count} gated, allow-listed)";
            if (missing.Count == 0)
            {
                Console.WriteLine($"\n-- {name}: ok, every referenced member exists{gatedNote}");
                foreach (Ref r in gated.OrderBy(m => m.Type).ThenBy(m => m.Name))
                    Console.WriteLine($"     gated  {Display(r)}  — {allowed[Display(r)]}");
                continue;
            }

            failures++;
            Console.WriteLine($"\n-- {name}: {missing.Count} MISSING{gatedNote}");
            foreach (Ref r in missing.OrderBy(m => m.Type).ThenBy(m => m.Name))
                Console.WriteLine($"     {Display(r)}");
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "PASS - the mod calls nothing these versions do not have."
            : $"FAIL - {failures} version(s) are missing something the mod calls.");
        return failures == 0 ? 0 : 1;
    }

    // How a member is written in this tool's output, and therefore in the allow list. One
    // formatter, so the two can never disagree about spacing.
    static string Display(Ref r) => $"{r.Type}.{r.Name}({r.Params})";

    // name | reason. Missing file = empty list, which fails everything, which is the safe way
    // round: an allow list that quietly vanished must not silently start passing things.
    static Dictionary<string, string> ReadAllowList(out string path)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        path = Path.Combine(AppContext.BaseDirectory, "allowed-missing.txt");
        if (!File.Exists(path))
        {
            // Running from the repo rather than from an output folder.
            string alt = Path.Combine("tools", "CmmApiDiff", "allowed-missing.txt");
            if (File.Exists(alt)) path = alt; else return d;
        }
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            int bar = line.IndexOf('|');
            if (bar <= 0) continue;
            d[line.Substring(0, bar).Trim()] = line.Substring(bar + 1).Trim();
        }
        return d;
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
            // METHODS AND FIELDS BOTH. Checking only methods let a field divergence through and
            // reported PASS while it was live: DynamicProperties.Values is a Dictionary on 3.x
            // and a DictionarySave on 2.6, so the typed field access did not resolve -- and it
            // threw inside a cell reader that catches, so the occlusion field silently stayed
            // empty and every speaker was inaudible. A tool that says PASS while that is true is
            // worse than no tool.
            MemberReferenceKind kind = mr.GetKind();
            if (kind != MemberReferenceKind.Method && kind != MemberReferenceKind.Field) continue;

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

            string parms;
            if (kind == MemberReferenceKind.Method)
            {
                MethodSignature<string> sig;
                try { sig = mr.DecodeMethodSignature(dec, null); }
                catch { continue; }
                parms = string.Join(",", sig.ParameterTypes);
            }
            else
            {
                // A field's "signature" is its TYPE, which is exactly what changed on 2.6 -- so it
                // has to be part of the key, not just the name.
                try { parms = "field:" + mr.DecodeFieldSignature(dec, null); }
                catch { continue; }
            }

            outp.Add(new Ref
            {
                Scope = scope ?? "?",
                Type = type,
                Name = md.GetString(mr.Name),
                Params = parms,
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

            foreach (FieldDefinitionHandle fh in td.GetFields())
            {
                FieldDefinition fd = md.GetFieldDefinition(fh);
                string ft;
                try { ft = fd.DecodeSignature(dec, null); }
                catch { continue; }
                set.Add($"{type}.{md.GetString(fd.Name)}(field:{ft})");
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
