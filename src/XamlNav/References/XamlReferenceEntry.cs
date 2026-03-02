using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.VisualStudio.Shell.TableControl;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Text.Classification;

namespace XamlNav
{
    /// <summary>
    /// Represents a single entry (row) in the Find All References window.
    /// Exposes standard column values consumed by VS's ITableManager infrastructure,
    /// including syntax-highlighted code snippets via WPF Inline elements.
    /// </summary>
    internal sealed class XamlReferenceEntry : ITableEntry
    {
        private readonly string _filePath;
        private readonly int _line;       // 0-based
        private readonly int _column;     // 0-based
        private readonly string _text;
        private readonly string _projectName;
        private readonly bool _isXaml;
        private readonly bool _isVerified;
        private readonly IClassificationFormatMap _formatMap;
        private readonly IClassificationTypeRegistryService _typeRegistry;

        /// <summary>
        /// Creates an entry from a Roslyn ReferenceLocation (C# code reference).
        /// </summary>
        public XamlReferenceEntry(
            ReferenceLocation refLocation,
            string projectName,
            IClassificationFormatMap formatMap,
            IClassificationTypeRegistryService typeRegistry)
        {
            var lineSpan = refLocation.Location.GetLineSpan();
            _filePath = lineSpan.Path;
            _line = lineSpan.StartLinePosition.Line;
            _column = lineSpan.StartLinePosition.Character;
            _projectName = projectName ?? string.Empty;
            _text = ReadSourceLine(_filePath, _line);
            _isXaml = false;
            _isVerified = true;
            _formatMap = formatMap;
            _typeRegistry = typeRegistry;
        }

        /// <summary>
        /// Creates an entry from a Roslyn Location (XAML binding reference).
        /// </summary>
        public XamlReferenceEntry(
            Location xamlLocation,
            string projectName,
            IClassificationFormatMap formatMap,
            IClassificationTypeRegistryService typeRegistry)
            : this(xamlLocation, projectName, true, formatMap, typeRegistry)
        {
        }

        /// <summary>
        /// Creates an entry from a Roslyn Location (XAML binding reference) with verification status.
        /// </summary>
        public XamlReferenceEntry(
            Location xamlLocation,
            string projectName,
            bool isVerified,
            IClassificationFormatMap formatMap,
            IClassificationTypeRegistryService typeRegistry)
        {
            var lineSpan = xamlLocation.GetLineSpan();
            _filePath = lineSpan.Path;
            _line = lineSpan.StartLinePosition.Line;
            _column = lineSpan.StartLinePosition.Character;
            _projectName = projectName ?? string.Empty;
            _text = ReadSourceLine(_filePath, _line);
            _isXaml = true;
            _isVerified = isVerified;
            _formatMap = formatMap;
            _typeRegistry = typeRegistry;
        }

        public object Identity => $"{_filePath}|{_line}|{_column}";

        public bool TryGetValue(string keyName, out object content)
        {
            switch (keyName)
            {
                case StandardTableColumnDefinitions.DocumentName:
                    content = _filePath;
                    return true;

                case StandardTableColumnDefinitions.Line:
                    content = _line;
                    return true;

                case StandardTableColumnDefinitions.Column:
                    content = _column;
                    return true;

                case StandardTableColumnDefinitions.Text:
                    content = _text;
                    return true;

                case StandardTableColumnDefinitions.ProjectName:
                    content = _projectName;
                    return true;

                case "textinlines":
                    content = CreateLineTextInlines();
                    return true;

                default:
                    content = null;
                    return false;
            }
        }

        public bool CanSetValue(string keyName) => false;

        public bool TrySetValue(string keyName, object content)
        {
            return false;
        }

        /// <summary>
        /// Creates WPF Inline elements with syntax-highlighted runs for the source line.
        /// Colors are theme-aware, sourced from VS's IClassificationFormatMap.
        /// </summary>
        public IList<Inline> CreateLineTextInlines()
        {
            var inlines = new List<Inline>();

            if (string.IsNullOrEmpty(_text))
            {
                inlines.Add(new Run(string.Empty));
                return inlines;
            }

            var tokens = _isXaml ? ClassifyXaml(_text) : ClassifyCSharp(_text);

            foreach (var token in tokens)
            {
                var run = new Run(token.Text);
                ApplyClassificationStyle(run, token.Classification);
                if (_isXaml && !_isVerified && run.Foreground is SolidColorBrush brush)
                {
                    var dimmed = new SolidColorBrush(Color.FromArgb(153, brush.Color.R, brush.Color.G, brush.Color.B));
                    dimmed.Freeze();
                    run.Foreground = dimmed;
                }
                inlines.Add(run);
            }

            return inlines;
        }

        /// <summary>
        /// Applies syntax coloring to a WPF Run.
        /// Strategy: Try VS classification first (primary name, then aliases).
        /// If no color resolved, use hardcoded theme-aware fallback colors.
        /// </summary>
        private void ApplyClassificationStyle(Run run, string classificationType)
        {
            // 1. Try VS classification format map (skip for our custom markup.* types)
            if (_formatMap != null && _typeRegistry != null
                && !classificationType.StartsWith("markup", StringComparison.OrdinalIgnoreCase))
            {
                // Try primary classification name
                var resolved = TryResolveVsBrush(classificationType);
                if (resolved != null)
                {
                    run.Foreground = resolved;
                    return;
                }

                // Try alternate VS classification names (e.g., "xml name" → "XML Name")
                if (_vsClassificationAliases.TryGetValue(classificationType, out var aliases))
                {
                    foreach (var alias in aliases)
                    {
                        resolved = TryResolveVsBrush(alias);
                        if (resolved != null)
                        {
                            run.Foreground = resolved;
                            return;
                        }
                    }
                }
            }

            // 2. Fallback to hardcoded theme-aware colors (always reached if VS lookup fails)
            var palette = IsDarkTheme() ? _darkPalette : _lightPalette;
            if (palette.TryGetValue(classificationType, out var fallbackBrush))
            {
                run.Foreground = fallbackBrush;
            }
        }

        /// <summary>
        /// Attempts to resolve a foreground brush from VS's classification format map.
        /// Returns null if the classification type is not found or has no foreground color.
        /// </summary>
        private SolidColorBrush TryResolveVsBrush(string typeName)
        {
            var classType = _typeRegistry.GetClassificationType(typeName);
            if (classType != null)
            {
                var props = _formatMap.GetTextProperties(classType);
                if (props.ForegroundBrushEmpty == false && props.ForegroundBrush is SolidColorBrush brush)
                {
                    return brush;
                }
            }
            return null;
        }

        // Maps our internal classification names to alternate VS classification type names.
        // VS may register XML/XAML classifications under various names depending on installed
        // language services, so we try several variants.
        // NOTE: Markup extension types (XM, MP, MV) are intentionally NOT listed here
        // so they always use our custom fallback palette colors.
        private static readonly Dictionary<string, string[]> _vsClassificationAliases =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                { "xml name",            new[] { "XML Name", "XAML Name" } },
                { "xml attribute",       new[] { "XML Attribute", "XAML Attribute" } },
                { "xml attribute value", new[] { "XML Attribute Value", "XAML Attribute Value", "string" } },
                { "xml delimiter",       new[] { "XML Delimiter", "XAML Delimiter", "punctuation" } },
                { "xml comment",         new[] { "XML Comment", "XAML Comment", "comment" } },
            };

        /// <summary>
        /// Detects if VS is using a dark theme by checking the tool window background luminance.
        /// Re-evaluated each time to handle live theme switches.
        /// </summary>
        private static bool IsDarkTheme()
        {
            try
            {
                var bgColor = Microsoft.VisualStudio.PlatformUI.VSColorTheme.GetThemedColor(
                    Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ToolWindowBackgroundColorKey);
                return (bgColor.R + bgColor.G + bgColor.B) / 3 < 128;
            }
            catch
            {
                return true; // Default to dark
            }
        }

        // Dark theme palette (VS Code-like colors)
        private static readonly Dictionary<string, SolidColorBrush> _darkPalette =
            new Dictionary<string, SolidColorBrush>(StringComparer.OrdinalIgnoreCase)
            {
                { "keyword",             Brush("#569CD6") },  // blue
                { "string",              Brush("#CE9178") },  // tan/orange
                { "comment",             Brush("#6A9955") },  // green
                { "number",              Brush("#B5CEA8") },  // light green
                { "identifier",          Brush("#DCDCAA") },  // yellow
                { "punctuation",         Brush("#D4D4D4") },  // light gray
                { "operator",            Brush("#D4D4D4") },  // light gray
                { "xml name",            Brush("#569CD6") },  // blue (element names)
                { "xml attribute",       Brush("#9CDCFE") },  // light blue (attributes)
                { "xml attribute value", Brush("#CE9178") },  // tan/orange (values)
                { "xml delimiter",       Brush("#D4D4D4") },  // light gray (delimiters)
                { "xml comment",         Brush("#6A9955") },  // green
                { "markup extension",    Brush("#BBA08C") },  // brownish orange (Binding, StaticResource)
                { "markup.param",        Brush("#D7BA7D") },  // brownish orange (Path=, AncestorType=)
                { "markup.value",        Brush("#D4D4D4") },  // light gray (DataContext.Title, Window)
                { "text",                Brush("#D4D4D4") },  // light gray
            };

        // Light theme palette (classic VS colors)
        private static readonly Dictionary<string, SolidColorBrush> _lightPalette =
            new Dictionary<string, SolidColorBrush>(StringComparer.OrdinalIgnoreCase)
            {
                { "keyword",             Brush("#0000FF") },  // blue
                { "string",              Brush("#A31515") },  // dark red
                { "comment",             Brush("#008000") },  // green
                { "number",              Brush("#098658") },  // teal
                { "identifier",          Brush("#001080") },  // dark blue
                { "punctuation",         Brush("#000000") },  // black
                { "operator",            Brush("#000000") },  // black
                { "xml name",            Brush("#A31515") },  // dark red (element names)
                { "xml attribute",       Brush("#FF0000") },  // red (attributes)
                { "xml attribute value", Brush("#0000FF") },  // blue (values)
                { "xml delimiter",       Brush("#0000FF") },  // blue
                { "xml comment",         Brush("#008000") },  // green
                { "markup extension",    Brush("#A31515") },  // dark red (Binding, StaticResource)
                { "markup.param",        Brush("#FF0000") },  // red (Path=, AncestorType=)
                { "markup.value",        Brush("#0000FF") },  // blue (DataContext.Title, Window)
                { "text",                Brush("#000000") },  // black
            };

        private static SolidColorBrush Brush(string hex)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        // ---------------------------------------------------------
        //  Token structure
        // ---------------------------------------------------------

        private struct ClassifiedToken
        {
            public string Classification;
            public string Text;
            public ClassifiedToken(string classification, string text)
            {
                Classification = classification;
                Text = text;
            }
        }

        // ---------------------------------------------------------
        //  C# lightweight classifier
        // ---------------------------------------------------------

        private static readonly HashSet<string> CSharpKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract","as","base","bool","break","byte","case","catch","char","checked",
            "class","const","continue","decimal","default","delegate","do","double","else",
            "enum","event","explicit","extern","false","finally","fixed","float","for",
            "foreach","goto","if","implicit","in","int","interface","internal","is","lock",
            "long","namespace","new","null","object","operator","out","override","params",
            "private","protected","public","readonly","ref","return","sbyte","sealed","short",
            "sizeof","stackalloc","static","string","struct","switch","this","throw","true",
            "try","typeof","uint","ulong","unchecked","unsafe","ushort","using","var",
            "virtual","void","volatile","while","async","await","get","set","value","yield",
            "partial","where","nameof","record","init","required","global","when"
        };

        // Classification type names matching VS editor classifications
        private const string CK = "keyword";
        private const string CS = "string";
        private const string CC = "comment";
        private const string CN = "number";
        private const string CI = "identifier";
        private const string CP = "punctuation";
        private const string CT = "text";

        private static List<ClassifiedToken> ClassifyCSharp(string line)
        {
            var tokens = new List<ClassifiedToken>();
            int i = 0;

            while (i < line.Length)
            {
                if (line[i] == '"')
                {
                    int end = FindEndOfString(line, i);
                    tokens.Add(new ClassifiedToken(CS, line.Substring(i, end - i)));
                    i = end;
                }
                else if (line[i] == '\'')
                {
                    int end = FindEndOfChar(line, i);
                    tokens.Add(new ClassifiedToken(CS, line.Substring(i, end - i)));
                    i = end;
                }
                else if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '/')
                {
                    tokens.Add(new ClassifiedToken(CC, line.Substring(i)));
                    i = line.Length;
                }
                else if (char.IsLetter(line[i]) || line[i] == '_' || line[i] == '@')
                {
                    int start = i;
                    if (line[i] == '@') i++;
                    while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_'))
                        i++;
                    string word = line.Substring(start, i - start);
                    string rawWord = word.StartsWith("@") ? word.Substring(1) : word;
                    tokens.Add(new ClassifiedToken(
                        CSharpKeywords.Contains(rawWord) ? CK : CI, word));
                }
                else if (char.IsDigit(line[i]))
                {
                    int start = i;
                    while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '.' || line[i] == '_'))
                        i++;
                    tokens.Add(new ClassifiedToken(CN, line.Substring(start, i - start)));
                }
                else if (char.IsWhiteSpace(line[i]))
                {
                    int start = i;
                    while (i < line.Length && char.IsWhiteSpace(line[i]))
                        i++;
                    tokens.Add(new ClassifiedToken(CT, line.Substring(start, i - start)));
                }
                else
                {
                    tokens.Add(new ClassifiedToken(CP, line[i].ToString()));
                    i++;
                }
            }

            return tokens;
        }

        private static int FindEndOfString(string line, int start)
        {
            int i = start + 1;
            while (i < line.Length)
            {
                if (line[i] == '\\') { i += 2; continue; }
                if (line[i] == '"') return i + 1;
                i++;
            }
            return line.Length;
        }

        private static int FindEndOfChar(string line, int start)
        {
            int i = start + 1;
            while (i < line.Length)
            {
                if (line[i] == '\\') { i += 2; continue; }
                if (line[i] == '\'') return i + 1;
                i++;
            }
            return line.Length;
        }

        // ---------------------------------------------------------
        //  XAML lightweight classifier
        // ---------------------------------------------------------

        // Classification type names for XAML (keys into palette dictionaries)
        private const string XN = "xml name";
        private const string XA = "xml attribute";
        private const string XV = "xml attribute value";
        private const string XD = "xml delimiter";
        private const string XC = "xml comment";
        private const string XM = "markup extension";   // Binding, StaticResource, x:Static
        private const string MP = "markup.param";        // Path=, AncestorType= (params inside {})
        private const string MV = "markup.value";        // DataContext.Title, Window (values inside {})

        /// <summary>
        /// Character-by-character XAML classifier that properly tokenizes markup extensions.
        /// Handles: element names, attribute names, attribute values, comments, 
        /// and markup extensions ({Binding Path=Prop} → keyword + attribute + identifier).
        /// </summary>
        private static List<ClassifiedToken> ClassifyXaml(string line)
        {
            var tokens = new List<ClassifiedToken>();
            int i = 0;

            while (i < line.Length)
            {
                // --- Comments ---
                if (i + 3 < line.Length && line.Substring(i, 4) == "<!--")
                {
                    int end = line.IndexOf("-->", i + 4);
                    if (end < 0) end = line.Length - 3;
                    end += 3;
                    tokens.Add(new ClassifiedToken(XC, line.Substring(i, end - i)));
                    i = end;
                }
                // --- Markup extension: {Binding ...} ---
                else if (line[i] == '{')
                {
                    ClassifyMarkupExtension(line, ref i, tokens);
                }
                // --- Tag open: < or </ ---
                else if (line[i] == '<')
                {
                    if (i + 1 < line.Length && line[i + 1] == '/')
                    {
                        tokens.Add(new ClassifiedToken(XD, "</"));
                        i += 2;
                    }
                    else
                    {
                        tokens.Add(new ClassifiedToken(XD, "<"));
                        i += 1;
                    }
                    // Element name follows
                    i = ReadXamlWord(line, i, tokens, XN);
                }
                // --- Tag end: /> or > (check /> first so it's not split) ---
                else if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '>')
                {
                    tokens.Add(new ClassifiedToken(XD, "/>"));
                    i += 2;
                }
                else if (line[i] == '>')
                {
                    tokens.Add(new ClassifiedToken(XD, ">"));
                    i++;
                }
                // --- Quoted attribute value ---
                else if (line[i] == '"')
                {
                    int end = line.IndexOf('"', i + 1);
                    if (end < 0) end = line.Length - 1;
                    string content = line.Substring(i + 1, end - i - 1);

                    // Check if the quoted value contains a markup extension
                    if (content.TrimStart().StartsWith("{") && !content.TrimStart().StartsWith("{}"))
                    {
                        tokens.Add(new ClassifiedToken(XD, "\""));  // opening quote
                        i++;
                        // Parse the content as XAML (will hit the { handler)
                        while (i < end)
                        {
                            if (line[i] == '{')
                            {
                                ClassifyMarkupExtension(line, ref i, tokens);
                            }
                            else if (char.IsWhiteSpace(line[i]))
                            {
                                int start = i;
                                while (i < end && char.IsWhiteSpace(line[i])) i++;
                                tokens.Add(new ClassifiedToken(CT, line.Substring(start, i - start)));
                            }
                            else
                            {
                                int start = i;
                                while (i < end && !char.IsWhiteSpace(line[i]) && line[i] != '{') i++;
                                tokens.Add(new ClassifiedToken(CI, line.Substring(start, i - start)));
                            }
                        }
                        tokens.Add(new ClassifiedToken(XD, "\""));  // closing quote
                        i = end + 1;
                    }
                    else
                    {
                        // Plain quoted value
                        tokens.Add(new ClassifiedToken(XV, line.Substring(i, end - i + 1)));
                        i = end + 1;
                    }
                }
                else if (line[i] == '\'')
                {
                    int end = line.IndexOf('\'', i + 1);
                    if (end < 0) end = line.Length - 1;
                    tokens.Add(new ClassifiedToken(XV, line.Substring(i, end - i + 1)));
                    i = end + 1;
                }
                // --- Equals sign ---
                else if (line[i] == '=')
                {
                    tokens.Add(new ClassifiedToken(XD, "="));
                    i++;
                }
                // --- Whitespace ---
                else if (char.IsWhiteSpace(line[i]))
                {
                    int start = i;
                    while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
                    tokens.Add(new ClassifiedToken(CT, line.Substring(start, i - start)));
                }
                // --- Word (attribute name or text) ---
                else if (char.IsLetterOrDigit(line[i]) || line[i] == '_' || line[i] == ':')
                {
                    int start = i;
                    while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_' || line[i] == ':' || line[i] == '.'))
                        i++;
                    string word = line.Substring(start, i - start);
                    // Check if followed by '=' → attribute name
                    int peek = i;
                    while (peek < line.Length && char.IsWhiteSpace(line[peek])) peek++;
                    if (peek < line.Length && line[peek] == '=')
                        tokens.Add(new ClassifiedToken(XA, word));
                    else
                        tokens.Add(new ClassifiedToken(XN, word));
                }
                // --- Other character ---
                else
                {
                    tokens.Add(new ClassifiedToken(XD, line[i].ToString()));
                    i++;
                }
            }

            return tokens;
        }

        /// <summary>
        /// Tokenizes a markup extension: {Binding Path=Value, Converter={StaticResource ...}}
        /// The first word after { is classified as keyword, properties as attributes.
        /// The first positional argument (before any comma or =) is the implicit binding path:
        ///   {Binding Test2}           → Test2 is markup.param
        ///   {Binding DataContext.Test2} → DataContext is markup.param, .Test2 is markup.value
        /// </summary>
        private static void ClassifyMarkupExtension(string line, ref int i, List<ClassifiedToken> tokens)
        {
            // Opening brace
            tokens.Add(new ClassifiedToken(XD, "{"));
            i++;

            // Skip whitespace
            SkipWhitespace(line, ref i, tokens);

            // Extension name (e.g., Binding, StaticResource, x:Static) → keyword
            if (i < line.Length && (char.IsLetter(line[i]) || line[i] == '_'))
            {
                int start = i;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_' || line[i] == ':'))
                    i++;
                tokens.Add(new ClassifiedToken(XM, line.Substring(start, i - start)));
            }

            // Track whether the first positional argument has been consumed.
            // Before we see any '=' or ',', the next word is the implicit path.
            bool firstPositionalConsumed = false;

            // Rest of the markup extension content
            while (i < line.Length && line[i] != '}')
            {
                if (char.IsWhiteSpace(line[i]))
                {
                    SkipWhitespace(line, ref i, tokens);
                }
                else if (line[i] == '{')
                {
                    // Nested markup extension
                    ClassifyMarkupExtension(line, ref i, tokens);
                    firstPositionalConsumed = true;
                }
                else if (line[i] == ',')
                {
                    tokens.Add(new ClassifiedToken(XD, ","));
                    i++;
                    firstPositionalConsumed = true;
                }
                else if (line[i] == '=')
                {
                    tokens.Add(new ClassifiedToken(XD, "="));
                    i++;
                    firstPositionalConsumed = true;
                }
                else if (char.IsLetterOrDigit(line[i]) || line[i] == '_' || line[i] == ':' || line[i] == '.')
                {
                    int start = i;
                    while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_' || line[i] == ':' || line[i] == '.'))
                        i++;
                    string word = line.Substring(start, i - start);

                    // Check if followed by '=' → parameter name
                    int peek = i;
                    while (peek < line.Length && char.IsWhiteSpace(line[peek])) peek++;
                    if (peek < line.Length && line[peek] == '=')
                    {
                        tokens.Add(new ClassifiedToken(MP, word));  // explicit param name (Path=, AncestorType=)
                    }
                    else if (!firstPositionalConsumed)
                    {
                        // First positional argument = implicit binding path
                        // Split on dots: first segment → param, rest → value
                        ClassifyDottedPath(word, tokens);
                        firstPositionalConsumed = true;
                    }
                    else
                    {
                        tokens.Add(new ClassifiedToken(MV, word));  // param value → light gray
                    }
                }
                else
                {
                    tokens.Add(new ClassifiedToken(XD, line[i].ToString()));
                    i++;
                }
            }

            // Closing brace
            if (i < line.Length && line[i] == '}')
            {
                tokens.Add(new ClassifiedToken(XD, "}"));
                i++;
            }
        }

        /// <summary>
        /// Classifies a dotted property path for implicit binding paths.
        /// First segment → markup.param, subsequent .Segments → markup.value.
        /// Examples:
        ///   "Test2"             → [MP:"Test2"]
        ///   "DataContext.Test2" → [MP:"DataContext", MV:".Test2"]
        /// </summary>
        private static void ClassifyDottedPath(string word, List<ClassifiedToken> tokens)
        {
            int dotIndex = word.IndexOf('.');
            if (dotIndex < 0)
            {
                // No dots — entire word is the root property path
                tokens.Add(new ClassifiedToken(MP, word));
            }
            else
            {
                // First segment is the root property
                tokens.Add(new ClassifiedToken(MP, word.Substring(0, dotIndex)));
                // Rest (including the dot) is the sub-property value
                tokens.Add(new ClassifiedToken(MV, word.Substring(dotIndex)));
            }
        }

        private static void SkipWhitespace(string line, ref int i, List<ClassifiedToken> tokens)
        {
            int start = i;
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            if (i > start)
                tokens.Add(new ClassifiedToken(CT, line.Substring(start, i - start)));
        }

        /// <summary>Reads a word (letters, digits, :, .) and adds it as a token with the given classification.</summary>
        private static int ReadXamlWord(string line, int i, List<ClassifiedToken> tokens, string classification)
        {
            // Skip any whitespace first
            while (i < line.Length && char.IsWhiteSpace(line[i]))
            {
                tokens.Add(new ClassifiedToken(CT, line[i].ToString()));
                i++;
            }

            if (i < line.Length && (char.IsLetter(line[i]) || line[i] == '_'))
            {
                int start = i;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_' || line[i] == ':' || line[i] == '.'))
                    i++;
                tokens.Add(new ClassifiedToken(classification, line.Substring(start, i - start)));
            }

            return i;
        }

        // ---------------------------------------------------------
        //  Helpers
        // ---------------------------------------------------------

        /// <summary>
        /// Reads a single line from a file on disk to provide the code snippet text.
        /// </summary>
        private static string ReadSourceLine(string filePath, int lineIndex)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                    return string.Empty;

                using (var reader = new StreamReader(filePath))
                {
                    string currentLine;
                    int currentIndex = 0;
                    while ((currentLine = reader.ReadLine()) != null)
                    {
                        if (currentIndex == lineIndex)
                            return currentLine.Trim();
                        currentIndex++;
                    }
                }
            }
            catch
            {
                // Silently ignore read errors
            }

            return string.Empty;
        }
    }
}
