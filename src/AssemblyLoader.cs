using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TryRoslynCompilation
{
    internal class AssemblyLoader
    {

        private readonly Dictionary<string, MetadataReference> _loadedAssemblies;
        private CSharpCompilation _cSharpCompilation;
        private readonly List<string> _assemblyDirs;

        internal AssemblyLoader()
        {
            _loadedAssemblies = new Dictionary<string, MetadataReference>();
            var compilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable);
            _cSharpCompilation = CSharpCompilation.Create($"AssemblyLoader_{DateTime.Now:MM_dd_yy_HH_mm_ss_FFF}", options: compilationOptions);
            _assemblyDirs = new List<string>();
        }

        internal bool HasDiagnostics(out IEnumerable<Diagnostic> diagnostics)
        {
            diagnostics = _cSharpCompilation.GetDiagnostics();
            return diagnostics.Any();
        }

        internal IEnumerable<IAssemblySymbol> LoadAssemblies(IEnumerable<AssemblyIdentity> identities)
        {
            List<IAssemblySymbol> matchingAssemblies = new List<IAssemblySymbol>();
            foreach (AssemblyIdentity unmappedIdentity in identities)
            {
                IAssemblySymbol matchingAssembly = LoadAssemblyFromIdentity(unmappedIdentity);

                if (matchingAssembly == null)
                {
                    // TODO: add error
                    continue;
                }

                if (!matchingAssembly.Identity.Version.Equals(unmappedIdentity.Version))
                {
                    // TODO: add warning
                    Console.WriteLine($"Found '{matchingAssembly.Identity.Name}' with version '{matchingAssembly.Identity.Version}' instead of '{unmappedIdentity.Version}'.");
                }

                string unmappedPkt = unmappedIdentity.HasPublicKey ? GetPublicKeyToken(unmappedIdentity.PublicKeyToken) : string.Empty;
                string matchingPkt = matchingAssembly.Identity.HasPublicKey ? GetPublicKeyToken(matchingAssembly.Identity.PublicKeyToken) : string.Empty;
                if (!matchingPkt.Equals(unmappedPkt))
                {
                    // TODO: add warning
                    Console.WriteLine($"Found '{matchingAssembly.Identity.Name}' with PublicKeyToken '{matchingPkt}' instead of '{unmappedPkt}'.");
                }

                matchingAssemblies.Add(matchingAssembly);
            }

            return matchingAssemblies;
        }

        private string GetPublicKeyToken(ImmutableArray<byte> publicKeyToken)
        {
            return string.Create(publicKeyToken.Length * 2, publicKeyToken, (dst, v) =>
            {
                for (int i = 0; i < publicKeyToken.Length; i++)
                {
                    Span<char> tmp = dst.Slice(i * 2, 2);
                    ReadOnlySpan<char> byteString = publicKeyToken[i].ToString("x2");
                    byteString.CopyTo(tmp);
                }
            });
        }

        private IAssemblySymbol LoadAssemblyFromIdentity(AssemblyIdentity unmappedIdentity)
        {
            foreach (string probeDir in _assemblyDirs)
            {
                string path = Path.Combine(probeDir, unmappedIdentity.Name + ".dll");

                if (File.Exists(path))
                {
                    MetadataReference reference = CreateMetadataReferenceIfNeeded(path);
                    ISymbol symbol = _cSharpCompilation.GetAssemblyOrModuleSymbol(reference);
                    if (symbol is IAssemblySymbol assemblySymbol)
                        return assemblySymbol;
                }
            }

            return null;
        }

        private string[] SplitPaths(string paths) => 
            paths == null ? Array.Empty<string>() : paths.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);

        internal void LoadReferences(string paths)
        {
            string[] assemblyPaths = SplitPaths(paths);

            if (assemblyPaths.Length == 0)
            {
                return;
            }

            LoadFromPaths(assemblyPaths);
        }

        internal IEnumerable<IAssemblySymbol> LoadAssemblies(string paths)
        {
            string[] assemblyPaths = SplitPaths(paths);

            if (assemblyPaths.Length == 0)
            {
                yield break;
            }

            IEnumerable<MetadataReference> assembliesToReturn = LoadFromPaths(assemblyPaths);

            foreach (MetadataReference assembly in assembliesToReturn)
            {
                ISymbol symbol = _cSharpCompilation.GetAssemblyOrModuleSymbol(assembly);
                if (symbol is IAssemblySymbol assemblySymbol)
                    yield return assemblySymbol;
            }
        }

        private IEnumerable<MetadataReference> LoadFromPaths(IEnumerable<string> paths)
        {
            List<MetadataReference> result = new List<MetadataReference>();
            foreach (string path in paths)
            {
                string resolvedPath = Environment.ExpandEnvironmentVariables(path);
                if (Directory.Exists(resolvedPath))
                {
                    _assemblyDirs.Add(resolvedPath);
                    result.AddRange(LoadAssembliesFromDirectory(resolvedPath));
                }
                else if (File.Exists(resolvedPath))
                {
                    _assemblyDirs.Add(Path.GetDirectoryName(resolvedPath));
                    result.Add(CreateMetadataReferenceIfNeeded(resolvedPath));
                }
            }

            return result;
        }

        private IEnumerable<MetadataReference> LoadAssembliesFromDirectory(string directory)
        {
            foreach (string assembly in Directory.EnumerateFiles(directory, "*.dll"))
            {
                yield return CreateMetadataReferenceIfNeeded(assembly);
            }
        }

        private MetadataReference CreateMetadataReferenceIfNeeded(string assembly)
        {
            // Roslyn doesn't support having two assemblies as references with the same identity and then getting the symbol for it.
            string fileName = Path.GetFileName(assembly);
            if (!_loadedAssemblies.TryGetValue(fileName, out MetadataReference reference))
            {
                reference = MetadataReference.CreateFromFile(assembly);
                _loadedAssemblies.Add(fileName, reference);
                _cSharpCompilation = _cSharpCompilation.AddReferences(new MetadataReference[] { reference });
            }

            return reference;
        }

    }
}
