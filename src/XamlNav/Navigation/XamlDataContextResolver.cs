using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace XamlNav
{
    /// <summary>
    /// Resolved DataContext information from XAML content.
    /// </summary>
    public class ResolvedDataContext
    {
        /// <summary>Full CLR type name, e.g. "MyApp.ViewModels.MainViewModel"</summary>
        public string FullTypeName { get; set; }

        /// <summary>XAML namespace prefix, e.g. "vm"</summary>
        public string Prefix { get; set; }

        /// <summary>Local type name without prefix, e.g. "MainViewModel"</summary>
        public string LocalName { get; set; }
    }

    /// <summary>
    /// Resolves the DataContext ViewModel type from XAML content at a given binding position.
    /// Supports 4 core patterns:
    ///   1. d:DesignInstance
    ///   2. Inline property element (e.g. Window.DataContext)
    ///   3. DataTemplate DataType="{x:Type ...}"
    ///   4. x:DataType (UWP/WinUI)
    /// </summary>
    public class XamlDataContextResolver
    {
        // Matches xmlns:prefix="clr-namespace:Namespace" or xmlns:prefix="clr-namespace:Namespace;assembly=Asm"
        private static readonly Regex XmlnsClrRegex = new Regex(
            @"xmlns:(?<prefix>\w+)\s*=\s*""clr-namespace:(?<ns>[^;""]+)(?:;assembly=(?<asm>[^""]*))?""",
            RegexOptions.Compiled);

        // Matches xmlns:prefix="using:Namespace" (UWP/WinUI style)
        private static readonly Regex XmlnsUsingRegex = new Regex(
            @"xmlns:(?<prefix>\w+)\s*=\s*""using:(?<ns>[^""]+)""",
            RegexOptions.Compiled);

        // Pattern 1a: d:DataContext="{d:DesignInstance ...}" with three type reference forms:
        //   Type=vm:MainViewModel
        //   Type={x:Type local:Class3}
        //   vm:MainViewModel  (positional, no Type=)
        private static readonly Regex DesignInstanceRegex = new Regex(
            @"d:DataContext\s*=\s*""\{d:DesignInstance\s+(?:Type=(?:\{x:Type\s+(?<type1>[\w:]+)\s*\}|(?<type2>[\w:]+))|(?<type3>[\w:]+))",
            RegexOptions.Compiled);

        // Pattern 1b: DataContext="{prefix:TypeName}" or d:DataContext="{prefix:TypeName}"
        // Matches a direct markup extension that is NOT a known system extension (Binding, StaticResource, etc.)
        private static readonly Regex DataContextDirectTypeRegex = new Regex(
            @"(?:d:)?DataContext\s*=\s*""\{(?<type>[\w]+:[\w]+)[\s}]",
            RegexOptions.Compiled);

        // Known markup extensions that are NOT direct type references
        private static readonly HashSet<string> KnownMarkupExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Binding", "x:Bind", "StaticResource", "DynamicResource", "x:Static",
            "x:Null", "x:Type", "TemplateBinding", "RelativeSource", "d:DesignInstance"
        };

        // Pattern 2: <Something.DataContext> followed by <prefix:TypeName ... />
        // We capture the position ranges to check containment.
        private static readonly Regex InlineDataContextRegex = new Regex(
            @"<\w+\.DataContext\s*>\s*<(?<type>[\w:]+)",
            RegexOptions.Compiled);

        // Pattern 3: DataType="{x:Type prefix:TypeName}"
        private static readonly Regex DataTypeXTypeRegex = new Regex(
            @"DataType\s*=\s*""\{x:Type\s+(?<type>[\w:]+)\s*\}""",
            RegexOptions.Compiled);

        // Pattern 4: x:DataType="prefix:TypeName"
        private static readonly Regex XDataTypeRegex = new Regex(
            @"x:DataType\s*=\s*""(?<type>[\w:]+)""",
            RegexOptions.Compiled);

        // Matches opening DataTemplate tags with their full content up to >
        private static readonly Regex DataTemplateOpenRegex = new Regex(
            @"<DataTemplate\b(?<attrs>[^>]*)>",
            RegexOptions.Compiled | RegexOptions.Singleline);

        // Matches closing DataTemplate tags
        private static readonly Regex DataTemplateCloseRegex = new Regex(
            @"</DataTemplate\s*>",
            RegexOptions.Compiled);

        // Pattern 5: DataContext = new SomeType() in code-behind
        private static readonly Regex CodeBehindDataContextRegex = new Regex(
            @"DataContext\s*=\s*new\s+(?<type>[\w.]+)\s*\(",
            RegexOptions.Compiled);

        // Common view suffixes to strip for naming convention resolution
        private static readonly string[] ViewSuffixes = { "View", "Window", "Page", "UserControl", "Dialog" };

        /// <summary>
        /// Resolves the DataContext type for a binding at the given position in the XAML content.
        /// Returns null if no DataContext can be determined.
        /// </summary>
        public ResolvedDataContext Resolve(string xamlContent, int bindingPosition)
        {
            return Resolve(xamlContent, bindingPosition, null);
        }

        /// <summary>
        /// Resolves the DataContext type for a binding, using XAML content analysis first,
        /// then falling back to code-behind analysis and naming conventions if a file path is provided.
        /// </summary>
        public ResolvedDataContext Resolve(string xamlContent, int bindingPosition, string xamlFilePath)
        {
            if (string.IsNullOrEmpty(xamlContent) || bindingPosition < 0 || bindingPosition >= xamlContent.Length)
                return null;

            var xmlnsMappings = ParseXmlnsMappings(xamlContent);

            // 1. Check if inside a DataTemplate with DataType or x:DataType
            var dataTemplateResult = TryResolveFromDataTemplate(xamlContent, bindingPosition, xmlnsMappings);
            if (dataTemplateResult != null)
                return dataTemplateResult;

            // 2. Check for d:DataContext="{d:DesignInstance ...}" on a parent element
            var designInstanceResult = TryResolveFromDesignInstance(xamlContent, bindingPosition, xmlnsMappings);
            if (designInstanceResult != null)
                return designInstanceResult;

            // 2b. Check for DataContext="{prefix:TypeName}" direct type reference
            var directTypeResult = TryResolveFromDirectDataContext(xamlContent, bindingPosition, xmlnsMappings);
            if (directTypeResult != null)
                return directTypeResult;

            // 3. Check for inline <T.DataContext> property element at root
            var inlineResult = TryResolveFromInlineDataContext(xamlContent, xmlnsMappings);
            if (inlineResult != null)
                return inlineResult;

            // 4. Check the code-behind file (.xaml.cs) for DataContext = new SomeType()
            if (!string.IsNullOrEmpty(xamlFilePath))
            {
                var codeBehindResult = TryResolveFromCodeBehind(xamlFilePath);
                if (codeBehindResult != null)
                    return codeBehindResult;

                // 5. Naming convention: FooView.xaml → FooViewModel
                var namingResult = TryResolveFromNamingConvention(xamlFilePath);
                if (namingResult != null)
                    return namingResult;
            }

            return null;
        }

        /// <summary>
        /// Parses all xmlns prefix mappings from the XAML content.
        /// Returns a dictionary mapping prefix → CLR namespace.
        /// </summary>
        internal static Dictionary<string, string> ParseXmlnsMappings(string xamlContent)
        {
            var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match match in XmlnsClrRegex.Matches(xamlContent))
            {
                string prefix = match.Groups["prefix"].Value;
                string ns = match.Groups["ns"].Value;
                mappings[prefix] = ns;
            }

            foreach (Match match in XmlnsUsingRegex.Matches(xamlContent))
            {
                string prefix = match.Groups["prefix"].Value;
                string ns = match.Groups["ns"].Value;
                if (!mappings.ContainsKey(prefix))
                    mappings[prefix] = ns;
            }

            return mappings;
        }

        /// <summary>
        /// Resolves a prefixed type name like "vm:MainViewModel" to a full CLR name
        /// using the xmlns mappings.
        /// </summary>
        internal static ResolvedDataContext ResolveTypeName(string prefixedType, Dictionary<string, string> xmlnsMappings)
        {
            if (string.IsNullOrEmpty(prefixedType))
                return null;

            string prefix;
            string localName;

            int colonIndex = prefixedType.IndexOf(':');
            if (colonIndex >= 0)
            {
                prefix = prefixedType.Substring(0, colonIndex);
                localName = prefixedType.Substring(colonIndex + 1);
            }
            else
            {
                // No prefix — cannot resolve to a full type name without default xmlns mapping
                prefix = null;
                localName = prefixedType;
            }

            if (prefix != null && xmlnsMappings.TryGetValue(prefix, out string ns))
            {
                return new ResolvedDataContext
                {
                    FullTypeName = ns + "." + localName,
                    Prefix = prefix,
                    LocalName = localName
                };
            }

            // If no mapping found, return with just the local name (partial info)
            if (prefix != null)
            {
                return new ResolvedDataContext
                {
                    FullTypeName = null,
                    Prefix = prefix,
                    LocalName = localName
                };
            }

            return null;
        }

        /// <summary>
        /// Checks if the binding position is inside a DataTemplate that has DataType or x:DataType.
        /// </summary>
        private ResolvedDataContext TryResolveFromDataTemplate(string content, int bindingPosition, Dictionary<string, string> xmlnsMappings)
        {
            // Find all DataTemplate open/close pairs and check if bindingPosition is inside one
            // We search for the innermost DataTemplate containing the position.
            ResolvedDataContext bestResult = null;
            int bestDistance = int.MaxValue;

            foreach (Match openMatch in DataTemplateOpenRegex.Matches(content))
            {
                int templateStart = openMatch.Index;
                if (templateStart > bindingPosition)
                    continue;

                string attrs = openMatch.Groups["attrs"].Value;
                int attrsOffset = openMatch.Groups["attrs"].Index;

                // Find the matching close tag (simple: find next </DataTemplate> after this open)
                var closeMatch = DataTemplateCloseRegex.Match(content, openMatch.Index + openMatch.Length);
                if (!closeMatch.Success)
                    continue;

                int templateEnd = closeMatch.Index + closeMatch.Length;
                if (bindingPosition < templateStart || bindingPosition > templateEnd)
                    continue;

                // The binding is inside this DataTemplate. Check for DataType or x:DataType.
                string typeName = null;

                // Try Pattern 3: DataType="{x:Type prefix:TypeName}"
                var xTypeMatch = DataTypeXTypeRegex.Match(attrs);
                if (xTypeMatch.Success)
                {
                    typeName = xTypeMatch.Groups["type"].Value;
                }

                // Try Pattern 4: x:DataType="prefix:TypeName"
                if (typeName == null)
                {
                    var xDataTypeMatch = XDataTypeRegex.Match(attrs);
                    if (xDataTypeMatch.Success)
                    {
                        typeName = xDataTypeMatch.Groups["type"].Value;
                    }
                }

                if (typeName != null)
                {
                    // Prefer the innermost (closest) DataTemplate
                    int distance = bindingPosition - templateStart;
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestResult = ResolveTypeName(typeName, xmlnsMappings);
                    }
                }
            }

            return bestResult;
        }

        /// <summary>
        /// Looks for d:DataContext="{d:DesignInstance ...}" on parent elements above the binding position.
        /// Returns the closest one found before the binding position.
        /// </summary>
        private ResolvedDataContext TryResolveFromDesignInstance(string content, int bindingPosition, Dictionary<string, string> xmlnsMappings)
        {
            ResolvedDataContext bestResult = null;
            int bestPosition = -1;

            foreach (Match match in DesignInstanceRegex.Matches(content))
            {
                if (match.Index < bindingPosition && match.Index > bestPosition)
                {
                    // Pick whichever capture group matched: type1 ({x:Type}), type2 (plain), or type3 (positional)
                    string typeName = match.Groups["type1"].Success ? match.Groups["type1"].Value
                                    : match.Groups["type2"].Success ? match.Groups["type2"].Value
                                    : match.Groups["type3"].Value;
                    var resolved = ResolveTypeName(typeName, xmlnsMappings);
                    if (resolved != null)
                    {
                        bestResult = resolved;
                        bestPosition = match.Index;
                    }
                }
            }

            return bestResult;
        }

        /// <summary>
        /// Checks for DataContext="{prefix:TypeName}" — a direct type markup extension.
        /// Filters out known system extensions (Binding, StaticResource, etc.).
        /// Also handles d:DataContext="{prefix:TypeName}".
        /// </summary>
        private ResolvedDataContext TryResolveFromDirectDataContext(string content, int bindingPosition, Dictionary<string, string> xmlnsMappings)
        {
            ResolvedDataContext bestResult = null;
            int bestPosition = -1;

            foreach (Match match in DataContextDirectTypeRegex.Matches(content))
            {
                if (match.Index >= bindingPosition || match.Index <= bestPosition)
                    continue;

                string typeName = match.Groups["type"].Value;

                // Filter out known markup extensions like "Binding:xxx" — but the typical
                // format is "{Binding ...}" without a colon, so the regex requiring
                // "prefix:Name" already excludes plain {Binding}. Still, skip d:DesignInstance etc.
                int colon = typeName.IndexOf(':');
                if (colon >= 0)
                {
                    string extensionName = typeName.Substring(0, colon) + ":" + typeName.Substring(colon + 1);
                    // Check if the whole "prefix:Name" is a known extension
                    if (KnownMarkupExtensions.Contains(extensionName))
                        continue;
                    // Also check just the prefix part (e.g. "d" in d:DesignInstance — already handled above)
                    // and the name part alone
                    string nameOnly = typeName.Substring(colon + 1);
                    if (KnownMarkupExtensions.Contains(nameOnly))
                        continue;
                }

                var resolved = ResolveTypeName(typeName, xmlnsMappings);
                if (resolved != null)
                {
                    bestResult = resolved;
                    bestPosition = match.Index;
                }
            }

            return bestResult;
        }

        /// <summary>
        /// Checks for inline DataContext property elements like:
        ///   &lt;Window.DataContext&gt;&lt;vm:MainViewModel /&gt;&lt;/Window.DataContext&gt;
        /// </summary>
        private ResolvedDataContext TryResolveFromInlineDataContext(string content, Dictionary<string, string> xmlnsMappings)
        {
            foreach (Match match in InlineDataContextRegex.Matches(content))
            {
                string typeName = match.Groups["type"].Value;
                var resolved = ResolveTypeName(typeName, xmlnsMappings);
                if (resolved != null)
                    return resolved;
            }

            return null;
        }

        /// <summary>
        /// Reads the code-behind file (.xaml.cs) and looks for
        /// <c>DataContext = new SomeType()</c> assignments.
        /// Returns a ResolvedDataContext with FullTypeName set to the type found
        /// (may be a short name like "MainViewModel" or a fully-qualified name).
        /// </summary>
        internal static ResolvedDataContext TryResolveFromCodeBehind(string xamlFilePath)
        {
            try
            {
                string codeBehindPath = xamlFilePath + ".cs";
                if (!File.Exists(codeBehindPath))
                    return null;

                string codeBehindContent = File.ReadAllText(codeBehindPath);

                var match = CodeBehindDataContextRegex.Match(codeBehindContent);
                if (match.Success)
                {
                    string typeName = match.Groups["type"].Value;

                    return new ResolvedDataContext
                    {
                        FullTypeName = typeName,
                        Prefix = null,
                        LocalName = typeName.Contains(".") ? typeName.Substring(typeName.LastIndexOf('.') + 1) : typeName
                    };
                }
            }
            catch
            {
                // Ignore read errors
            }

            return null;
        }

        /// <summary>
        /// Applies naming conventions to infer the ViewModel type from the XAML file name.
        /// E.g. MainView.xaml → MainViewModel, OrderWindow.xaml → OrderWindowViewModel.
        /// Returns a ResolvedDataContext with FullTypeName set to the short ViewModel name.
        /// </summary>
        internal static ResolvedDataContext TryResolveFromNamingConvention(string xamlFilePath)
        {
            string fileNameWithoutExt = Path.GetFileNameWithoutExtension(xamlFilePath);
            if (string.IsNullOrEmpty(fileNameWithoutExt))
                return null;

            // Try stripping known view suffixes: MainView → Main → MainViewModel
            foreach (var suffix in ViewSuffixes)
            {
                if (fileNameWithoutExt.EndsWith(suffix, StringComparison.Ordinal) && fileNameWithoutExt.Length > suffix.Length)
                {
                    string baseName = fileNameWithoutExt.Substring(0, fileNameWithoutExt.Length - suffix.Length);
                    string vmName = baseName + "ViewModel";
                    return new ResolvedDataContext
                    {
                        FullTypeName = vmName,
                        Prefix = null,
                        LocalName = vmName
                    };
                }
            }

            // Fallback: just append "ViewModel" (e.g. Main.xaml → MainViewModel)
            string fallbackVmName = fileNameWithoutExt + "ViewModel";
            return new ResolvedDataContext
            {
                FullTypeName = fallbackVmName,
                Prefix = null,
                LocalName = fallbackVmName
            };
        }

    }
}

