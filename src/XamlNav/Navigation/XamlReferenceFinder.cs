using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Text;

namespace XamlNav
{
    /// <summary>
    /// A XAML binding reference with verification status.
    /// </summary>
    public class XamlBindingReference
    {
        public Location Location { get; set; }

        /// <summary>
        /// True if the DataContext was resolved and matches the property's containing type.
        /// False if the DataContext could not be determined (unverified).
        /// </summary>
        public bool IsVerified { get; set; }
    }

    /// <summary>
    /// Finds references to symbols within XAML files.
    /// </summary>
    [Export(typeof(XamlReferenceFinder))]
    public class XamlReferenceFinder
    {
        private readonly VisualStudioWorkspace _workspace;
        private readonly XamlDataContextResolver _resolver = new XamlDataContextResolver();

        [ImportingConstructor]
        public XamlReferenceFinder(VisualStudioWorkspace workspace)
        {
            _workspace = workspace;
        }

        /// <summary>
        /// Finds references to the given symbol in all XAML files in the solution.
        /// Returns XamlBindingReference items with verification status.
        /// </summary>
        public async Task<IEnumerable<XamlBindingReference>> FindReferencesAsync(ISymbol symbol)
        {
            if (symbol == null || !(symbol is IPropertySymbol property))
                return Enumerable.Empty<XamlBindingReference>();

            var results = new List<XamlBindingReference>();
            string propertyName = property.Name;
            var processedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Iterate through Roslyn documents (catches open files with unsaved changes)
            foreach (var project in _workspace.CurrentSolution.Projects)
            {
                foreach (var document in project.Documents)
                {
                    if (document.FilePath != null && Path.GetExtension(document.FilePath).Equals(".xaml", StringComparison.OrdinalIgnoreCase))
                    {
                        if (processedFiles.Add(document.FilePath))
                        {
                            await FindReferencesInXamlFileAsync(document.FilePath, propertyName, property, results);
                        }
                    }
                }

                foreach (var document in project.AdditionalDocuments)
                {
                    if (document.FilePath != null && Path.GetExtension(document.FilePath).Equals(".xaml", StringComparison.OrdinalIgnoreCase))
                    {
                        if (processedFiles.Add(document.FilePath))
                        {
                            await FindReferencesInXamlFileAsync(document.FilePath, propertyName, property, results);
                        }
                    }
                }

                // 2. Filesystem Fallback: Scan project directory for all .xaml files
                // This catches files not yet loaded into the Roslyn workspace
                if (!string.IsNullOrEmpty(project.FilePath))
                {
                    string projectDir = Path.GetDirectoryName(project.FilePath);
                    if (Directory.Exists(projectDir))
                    {
                        try
                        {
                            var xamlFiles = Directory.EnumerateFiles(projectDir, "*.xaml", SearchOption.AllDirectories);
                            foreach (var file in xamlFiles)
                            {
                                if (XamlNavigationHelper.ShouldSkipFile(file)) continue;

                                if (processedFiles.Add(file))
                                {
                                    await FindReferencesInXamlFileAsync(file, propertyName, property, results);
                                }
                            }
                        }
                        catch (Exception)
                        {
                            // Ignore access errors etc.
                        }
                    }
                }
            }

            return results;
        }

        private async Task FindReferencesInXamlFileAsync(string filePath, string propertyName, IPropertySymbol property, List<XamlBindingReference> results)
        {
            try
            {
                if (!File.Exists(filePath)) return;

                string content = File.ReadAllText(filePath);
                var bindings = XamlBindingParser.ParseBindings(content);

                foreach (var binding in bindings)
                {
                    if (IsMatch(binding.PropertyName, propertyName))
                    {
                        // Try to resolve the DataContext at this binding's position
                        var resolved = _resolver.Resolve(content, binding.BindingExpressionStart, filePath);

                        bool isVerified;
                        if (resolved != null && resolved.FullTypeName != null)
                        {
                            // DataContext was resolved — check if the resolved type
                            // is or inherits from the property's ContainingType.
                            // This ensures Class3.Title doesn't match when we search Class2.Title.
                            bool? compatible = await IsResolvedTypeCompatibleAsync(resolved.FullTypeName, property.ContainingType);
                            if (compatible == false)
                            {
                                // Resolved to a definitely incompatible type → discard
                                continue;
                            }
                            // true = verified match, null = type not in solution → unverified
                            isVerified = (compatible == true);
                        }
                        else
                        {
                            // Could not determine DataContext → unverified
                            isVerified = false;
                        }

                        var textSpan = new Microsoft.CodeAnalysis.Text.TextSpan(binding.StartPosition, binding.Length);
                        var lineSpan = GetLineSpan(content, binding.StartPosition, binding.Length, filePath);
                        var location = Location.Create(filePath, textSpan, lineSpan.Span);

                        results.Add(new XamlBindingReference
                        {
                            Location = location,
                            IsVerified = isVerified
                        });
                    }
                }
            }
            catch (Exception)
            {
                // Skip files we can't read
            }
        }

        /// <summary>
        /// Checks if the XAML-resolved DataContext type is compatible with the property's containing type.
        /// Compatible means the resolved type IS the containing type, or INHERITS from it, or IMPLEMENTS it.
        /// This prevents Class3.Title from matching when we're searching for Class2.Title.
        /// Returns: true = verified match, false = definitely incompatible (discard), null = can't determine (unverified).
        /// </summary>
        private async Task<bool?> IsResolvedTypeCompatibleAsync(string resolvedFullTypeName, INamedTypeSymbol propertyContainingType)
        {
            return await IsResolvedTypeCompatibleAsync(resolvedFullTypeName, propertyContainingType, _workspace);
        }

        /// <summary>
        /// Static overload for reuse by other commands (e.g. Rename).
        /// </summary>
        internal static async Task<bool?> IsResolvedTypeCompatibleAsync(string resolvedFullTypeName, INamedTypeSymbol propertyContainingType, Workspace workspace)
        {
            var resolvedType = await FindTypeInSolutionAsync(resolvedFullTypeName, workspace);
            if (resolvedType == null)
            {
                // Type not found in solution — can't verify either way → unverified
                return null;
            }

            return IsTypeOrDescendant(resolvedType, propertyContainingType);
        }

        /// <summary>
        /// Checks if resolvedType is the same as targetType, inherits from it, or implements it.
        /// This handles the case where FooViewModel : BaseViewModel and the property is on BaseViewModel.
        /// </summary>
        internal static bool IsTypeOrDescendant(INamedTypeSymbol resolvedType, INamedTypeSymbol targetType)
        {
            // Walk the resolved type's inheritance chain
            var current = resolvedType;
            while (current != null)
            {
                if (SymbolEqualityComparer.Default.Equals(current, targetType))
                    return true;
                current = current.BaseType;
            }

            // Check interfaces
            foreach (var iface in resolvedType.AllInterfaces)
            {
                if (SymbolEqualityComparer.Default.Equals(iface, targetType))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Searches the Roslyn workspace for an INamedTypeSymbol matching the given type name.
        /// Tries exact metadata name first, then falls back to searching all types by short name
        /// (needed for code-behind and naming-convention resolution which may produce short names).
        /// </summary>
        private async Task<INamedTypeSymbol> FindTypeInSolutionAsync(string typeName)
        {
            return await FindTypeInSolutionAsync(typeName, _workspace);
        }

        /// <summary>
        /// Static overload for reuse by other commands (e.g. Rename).
        /// </summary>
        internal static async Task<INamedTypeSymbol> FindTypeInSolutionAsync(string typeName, Workspace workspace)
        {
            // 1. Try exact metadata name lookup (fast path for fully-qualified names)
            foreach (var project in workspace.CurrentSolution.Projects)
            {
                var compilation = await project.GetCompilationAsync();
                if (compilation == null) continue;

                var typeSymbol = compilation.GetTypeByMetadataName(typeName);
                if (typeSymbol != null)
                    return typeSymbol;
            }

            // 2. If not found and it looks like a short name (no dots), search all types
            if (!typeName.Contains("."))
            {
                foreach (var project in workspace.CurrentSolution.Projects)
                {
                    var compilation = await project.GetCompilationAsync();
                    if (compilation == null) continue;

                    var found = FindTypeByShortName(compilation.GlobalNamespace, typeName);
                    if (found != null)
                        return found;
                }
            }

            return null;
        }

        /// <summary>
        /// Recursively searches namespace symbols for a type matching the short name.
        /// </summary>
        internal static INamedTypeSymbol FindTypeByShortName(INamespaceSymbol ns, string shortName)
        {
            foreach (var type in ns.GetTypeMembers(shortName))
            {
                return type; // Return first match
            }

            foreach (var childNs in ns.GetNamespaceMembers())
            {
                var found = FindTypeByShortName(childNs, shortName);
                if (found != null)
                    return found;
            }

            return null;
        }

        public static bool IsMatch(string bindingPath, string targetPropertyName)
        {
            if (string.IsNullOrEmpty(bindingPath) || string.IsNullOrEmpty(targetPropertyName))
                return false;

            // Check if the property name exists as a segment in the binding path (e.g. "Items.MyProp")
            string[] segments = bindingPath.Split('.');
            foreach (var segment in segments)
            {
                if (segment == targetPropertyName)
                {
                    return true;
                }
            }

            return false;
        }

        private Microsoft.CodeAnalysis.FileLinePositionSpan GetLineSpan(string content, int start, int length, string filePath)
        {
            int line = 0;
            int character = 0;

            for (int i = 0; i < start; i++)
            {
                if (content[i] == '\n')
                {
                    line++;
                    character = 0;
                }
                else if (content[i] != '\r')
                {
                    character++;
                }
            }

            var startPos = new Microsoft.CodeAnalysis.Text.LinePosition(line, character);

            // Re-calculate for end (simple approximation)
            for (int i = start; i < start + length; i++)
            {
                if (content[i] == '\n')
                {
                    line++;
                    character = 0;
                }
                else if (content[i] != '\r')
                {
                    character++;
                }
            }
            var endPos = new Microsoft.CodeAnalysis.Text.LinePosition(line, character);

            return new Microsoft.CodeAnalysis.FileLinePositionSpan(filePath, startPos, endPos);
        }
    }
}
