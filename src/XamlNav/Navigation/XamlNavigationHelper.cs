using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Task = System.Threading.Tasks.Task;

namespace XamlNav
{
    /// <summary>
    /// Shared helper class for XAML navigation functionality.
    /// Provides common logic for detecting navigable positions and executing navigation.
    /// </summary>
    public static class XamlNavigationHelper
    {
        // Patterns for navigable XAML elements
        private static readonly string[] NavigablePatterns = new[]
        {
            "Binding",
            "x:Bind",
            "x:Type",
            "StaticResource",
            "DynamicResource",
            "d:DesignInstance",
            "DesignInstance",
            "x:Static"
        };

        // Pre-compiled regexes (hot path — called on every mouse move with Ctrl held)
        private static readonly Regex XClassRegex = new Regex(@"x:Class\s*=\s*""([^""]+)""", RegexOptions.Compiled);
        private static readonly Regex SourceAttrRegex = new Regex(@"(?<![a-zA-Z])Source\s*=\s*""([^""]+)""", RegexOptions.Compiled);
        private static readonly Regex AttrValueRegex = new Regex(@"(?<![a-zA-Z0-9_:!])([a-zA-Z_][a-zA-Z0-9_]*)\s*=\s*""([^""]*)""", RegexOptions.Compiled);
        private static readonly Regex IdentifierRegex = new Regex(@"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.Compiled);
        private static readonly Regex TagNameRegex = new Regex(@"<[/?!]*([a-zA-Z_][a-zA-Z0-9._:]*)", RegexOptions.Compiled);

        /// <summary>
        /// Determines if the given position is inside a navigable XAML element.
        /// Supports: {Binding}, {x:Bind}, {x:Type}, {StaticResource}, {DynamicResource}, {d:DesignInstance}, x:Class, Source attribute
        /// </summary>
        public static bool IsNavigablePosition(SnapshotPoint point)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var line = point.GetContainingLine();
                string text = line.GetText();
                int indexOnLine = point.Position - line.Start.Position;

                return IsNavigablePosition(text, indexOnLine);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[IsNavigablePosition] Exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Testable overload: Determines if the given position in a text line is inside a navigable element.
        /// </summary>
        public static bool IsNavigablePosition(string text, int indexOnLine)
        {
            // Safety check
            if (string.IsNullOrEmpty(text) || indexOnLine < 0 || indexOnLine > text.Length)
                return false;

            // Check for x:Class attribute (not inside braces)
            if (IsInsideXClass(text, indexOnLine))
            {
                DebugLogger.LogSync("[IsNavigablePosition] Found x:Class attribute");
                return true;
            }

            // Check for Source attribute (e.g., ResourceDictionary Source="/Dictionary1.xaml")
            if (IsInsideSourceAttribute(text, indexOnLine))
            {
                DebugLogger.LogSync("[IsNavigablePosition] Found Source attribute");
                return true;
            }

            // Check for XAML tag names (e.g., <local:UserControl1 or </local:UserControl1>)
            if (IsInsideTagName(text, indexOnLine))
            {
                DebugLogger.LogSync("[IsNavigablePosition] Found tag name");
                return true;
            }

            // Check for event handlers (e.g., Click="Button_Click" or MouseDoubleClick="Handler")
            if (IsInsideEventHandler(text, indexOnLine))
            {
                DebugLogger.LogSync("[IsNavigablePosition] Found event handler");
                return true;
            }

            // Check for markup extensions inside braces
            int searchIndex = (indexOnLine == text.Length) ? indexOnLine - 1 : indexOnLine;
            if (searchIndex < 0) return false;

            int lastOpenBrace = text.LastIndexOf('{', searchIndex);
            int nextCloseBrace = text.IndexOf('}', searchIndex);

            if (lastOpenBrace >= 0 && nextCloseBrace > lastOpenBrace)
            {
                string content = text.Substring(lastOpenBrace, nextCloseBrace - lastOpenBrace + 1);

                foreach (var pattern in NavigablePatterns)
                {
                    if (content.Contains(pattern))
                    {
                        DebugLogger.LogSync($"[IsNavigablePosition] Found navigable pattern: {pattern}");
                        return true;
                    }
                }

                // Fallback for custom markup extensions: {prefix:ExtensionName ...} or {ExtensionName ...}
                // We check if the content starts with an identifier (possibly with prefix)
                if (Regex.IsMatch(content, @"^\{[a-zA-Z_][a-zA-Z0-9._:]*"))
                {
                    DebugLogger.LogSync("[IsNavigablePosition] Found custom markup extension");
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks if the position is inside an x:Class attribute value.
        /// </summary>
        internal static bool IsInsideXClass(string lineText, int indexOnLine)
        {
            // Pattern: x:Class="Namespace.ClassName"
            var match = XClassRegex.Match(lineText);
            if (match.Success)
            {
                int valueStart = match.Index + match.Value.IndexOf('"') + 1;
                int valueEnd = match.Index + match.Value.LastIndexOf('"');

                return indexOnLine >= valueStart && indexOnLine <= valueEnd;
            }
            return false;
        }

        /// <summary>
        /// Checks if the position is inside a Source attribute value.
        /// Supports: ResourceDictionary Source="/Dictionary1.xaml", Image Source, etc.
        /// Does NOT match ItemsSource, DataSource, or other *Source attributes.
        /// </summary>
        internal static bool IsInsideSourceAttribute(string lineText, int indexOnLine)
        {
            // Pattern: Source="path/to/file.xaml" - must be standalone "Source", not "ItemsSource" etc.
            // Use negative lookbehind to ensure "Source" is not preceded by a letter
            var match = SourceAttrRegex.Match(lineText);
            if (match.Success)
            {
                int valueStart = match.Index + match.Value.IndexOf('"') + 1;
                int valueEnd = match.Index + match.Value.LastIndexOf('"');

                return indexOnLine >= valueStart && indexOnLine <= valueEnd;
            }
            return false;
        }

        /// <summary>
        /// Checks if the position is inside an event handler attribute value.
        /// Uses heuristics to match attribute names that look like events (e.g., Click, Mouse..., ...Changed).
        /// </summary>
        internal static bool IsInsideEventHandler(string lineText, int indexOnLine)
        {
            // Match Attribute="Value"
            var matches = AttrValueRegex.Matches(lineText);
            foreach (Match match in matches)
            {
                int valueStart = match.Index + match.Value.IndexOf('"') + 1;
                int valueEnd = match.Index + match.Value.LastIndexOf('"');

                if (indexOnLine >= valueStart && indexOnLine <= valueEnd)
                {
                    string attrName = match.Groups[1].Value;
                    string attrValue = match.Groups[2].Value;

                    // Verify the attribute name looks like an event
                    if (!IsLikeEventAttribute(attrName))
                        continue;

                    // Event handlers are identifiers (no dots, no markup extensions)
                    if (IdentifierRegex.IsMatch(attrValue))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        internal static bool IsLikeEventAttribute(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            // Common prefixes
            if (name.StartsWith("Click") ||
                name.StartsWith("Preview") ||
                name.StartsWith("Mouse") ||
                name.StartsWith("Key") ||
                name.StartsWith("Touch") ||
                name.StartsWith("Stylus") ||
                name.StartsWith("Drag") ||
                name.StartsWith("Drop") ||
                name.StartsWith("Manipulation") ||
                name.StartsWith("Pointer") ||
                name.StartsWith("Gesture"))
                return true;

            // Common suffixes
            if (name.EndsWith("Click") ||
                name.EndsWith("Changed") ||
                name.EndsWith("Loaded") ||
                name.EndsWith("Unloaded") ||
                name.EndsWith("Initialized") ||
                name.EndsWith("Opened") ||
                name.EndsWith("Closed") ||
                name.EndsWith("Completed") ||
                name.EndsWith("Down") ||
                name.EndsWith("Up") ||
                name.EndsWith("Enter") ||
                name.EndsWith("Leave") ||
                name.EndsWith("Move") ||
                name.EndsWith("Started") ||
                name.EndsWith("View") ||
                name.EndsWith("Query") ||
                name.EndsWith("Change"))
                return true;

            // Exact matches
            if (name == "GotFocus" || name == "LostFocus" ||
                name == "Tap" || name == "DoubleTap" ||
                name == "Holding" || name == "RightTapped")
                return true;

            return false;
        }

        /// <summary>
        /// Checks if the position is inside a XAML tag name (at the start of a tag).
        /// </summary>
        internal static bool IsInsideTagName(string lineText, int indexOnLine)
        {
            // Regex to find tag start: < followed by optional / and then the name
            // Capture only the name part
            var matches = TagNameRegex.Matches(lineText);
            foreach (Match match in matches)
            {
                Group group = match.Groups[1];
                if (indexOnLine >= group.Index && indexOnLine <= group.Index + group.Length)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Gets the span of the word/identifier at the given position.
        /// </summary>
        public static SnapshotSpan? GetWordSpan(SnapshotPoint point)
        {
            try
            {
                var line = point.GetContainingLine();
                string text = line.GetText();
                int indexOnLine = point.Position - line.Start.Position;

                if (GetWordBoundaries(text, indexOnLine, out int start, out int end))
                {
                    return new SnapshotSpan(line.Start + start, end - start);
                }
                return null;
            }
            catch
            {
                // Silently fail — callers handle null returns
                return null;
            }
        }

        internal static bool GetWordBoundaries(string text, int indexOnLine, out int start, out int end)
        {
            start = -1;
            end = -1;

            if (string.IsNullOrEmpty(text) || indexOnLine < 0 || indexOnLine >= text.Length)
                return false;

            // Find word boundaries (alphanumeric, underscore)
            start = indexOnLine;
            end = indexOnLine;

            // Move start backwards
            while (start > 0 && IsWordChar(text[start - 1]))
                start--;

            // Move end forwards
            while (end < text.Length && IsWordChar(text[end]))
                end++;

            return start != end;
        }

        private static bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }

        /// <summary>
        /// Highlights the word at the given position and executes Go To Definition.
        /// </summary>
        public static void HighlightAndNavigate(IWpfTextView view, SnapshotPoint point)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Get word span for highlighting
            var wordSpan = GetWordSpan(point);
            if (wordSpan.HasValue)
            {
                // Select the word to highlight it
                view.Selection.Select(wordSpan.Value, false);
                view.Caret.MoveTo(wordSpan.Value.Start); // Move to start to highlight better
                string selectedWord = wordSpan.Value.GetText();

                // Check for namespace prefix: if a colon follows the word, we handle it manually
                var snapshot = view.TextSnapshot;
                int afterWordIndex = wordSpan.Value.End.Position;
                if (afterWordIndex < snapshot.Length && snapshot[afterWordIndex] == ':')
                {
                    DebugLogger.Log($"[HighlightAndNavigate] prefix detected: '{selectedWord}', jumping to xmlns declaration.");
                    JumpToNamespaceDeclaration(view, selectedWord);
                }
                else
                {
                    // Regular navigation for properties, resources, and types
                    DebugLogger.Log($"[HighlightAndNavigate] Selected word: '{selectedWord}', calling Navigate.");
                    Navigate();
                }
            }
            else
            {
                // Just move caret if no word found
                view.Caret.MoveTo(point);
                Navigate();
            }
        }

        private static void JumpToNamespaceDeclaration(IWpfTextView view, string prefix)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string searchText = $"xmlns:{prefix}=";
            string snapshotText = view.TextSnapshot.GetText();
            int index = snapshotText.IndexOf(searchText, StringComparison.OrdinalIgnoreCase);

            if (index != -1)
            {
                var jumpPoint = new SnapshotPoint(view.TextSnapshot, index);
                view.Caret.MoveTo(jumpPoint);
                view.ViewScroller.EnsureSpanVisible(new SnapshotSpan(jumpPoint, searchText.Length));
                DebugLogger.Log($"[JumpToNamespaceDeclaration] Found '{searchText}' at index {index}. Jumped.");
            }
            else
            {
                DebugLogger.Log($"[JumpToNamespaceDeclaration] Could not find '{searchText}' in current document.");
            }
        }

        /// <summary>
        /// Executes the Go To Definition command.
        /// </summary>
        public static void Navigate()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (Package.GetGlobalService(typeof(SVsUIShell)) is IVsUIShell shell)
            {
                object args = null;
                Guid cmdGroup = VSConstants.GUID_VSStandardCommandSet97;
                uint cmdId = (uint)VSConstants.VSStd97CmdID.GotoDefn;

                shell.PostExecCommand(ref cmdGroup, cmdId, 0, ref args);
            }
        }

        /// <summary>
        /// Updates the cursor based on whether we're hovering over a navigable element with Ctrl held.
        /// </summary>
        public static void UpdateCursor(SnapshotPoint? point, bool ctrlPressed)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (ctrlPressed && point.HasValue && IsNavigablePosition(point.Value))
            {
                Mouse.OverrideCursor = Cursors.Hand;
            }
            else
            {
                Mouse.OverrideCursor = null;
            }
        }

        // ---------------------------------------------------------
        //  Shared utilities (used by Find/Rename commands)
        // ---------------------------------------------------------

        /// <summary>
        /// Returns true if the file path is inside a build/temp directory that should
        /// be excluded from XAML reference scanning (bin, obj, .vs, .git).
        /// </summary>
        public static bool ShouldSkipFile(string filePath)
        {
            string lower = filePath.ToLowerInvariant();
            return lower.Contains("\\bin\\") || lower.Contains("/bin/") ||
                   lower.Contains("\\obj\\") || lower.Contains("/obj/") ||
                   lower.Contains("\\.vs\\") || lower.Contains("/.vs/") ||
                   lower.Contains("\\.git\\") || lower.Contains("/.git/");
        }

        /// <summary>
        /// Extracts the identifier under the caret in a XAML editor and searches the
        /// Roslyn solution for a matching C# property symbol.
        /// Returns null if no match is found.
        /// </summary>
        public static async Task<ISymbol> FindSymbolFromXamlCursorAsync(
            SnapshotPoint caretPosition,
            Workspace workspace)
        {
            var wordSpan = GetWordSpan(caretPosition);
            if (!wordSpan.HasValue) return null;

            string propertyName = wordSpan.Value.GetText();
            if (string.IsNullOrWhiteSpace(propertyName)) return null;

            await DebugLogger.LogAsync($"[XamlNav] XAML mode: searching for property '{propertyName}' in solution.");

            foreach (var project in workspace.CurrentSolution.Projects)
            {
                foreach (var doc in project.Documents)
                {
                    if (!doc.SupportsSyntaxTree) continue;

                    var root = await doc.GetSyntaxRootAsync();
                    if (root == null) continue;

                    var propDecls = root.DescendantNodes()
                        .OfType<PropertyDeclarationSyntax>()
                        .Where(p => p.Identifier.Text == propertyName);

                    foreach (var propDecl in propDecls)
                    {
                        var semanticModel = await doc.GetSemanticModelAsync();
                        var sym = semanticModel?.GetDeclaredSymbol(propDecl);
                        if (sym != null) return sym;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Resolves the active text view and the symbol under the caret.
        /// Dual-path: uses Roslyn for C# documents, word-extraction for XAML.
        /// Returns (null, null) if resolution fails at any step.
        /// </summary>
        public static async Task<(IWpfTextView View, ISymbol Symbol)> ResolveActiveViewAndSymbolAsync(
            AsyncPackage package,
            Workspace workspace)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var textManager = await package.GetServiceAsync(typeof(SVsTextManager)) as IVsTextManager;
            if (textManager == null) return (null, null);

            textManager.GetActiveView(1, null, out IVsTextView textView);
            if (textView == null) return (null, null);

            var componentModel = await package.GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
            if (componentModel == null) return (null, null);
            var editorAdapterFactory = componentModel.GetService<IVsEditorAdaptersFactoryService>();
            var wpfTextView = editorAdapterFactory.GetWpfTextView(textView);
            if (wpfTextView == null) return (null, null);

            var caretPosition = wpfTextView.Caret.Position.BufferPosition;

            Document document = caretPosition.Snapshot.GetOpenDocumentInCurrentContextWithChanges();

            ISymbol symbol = null;

            if (document != null && document.SupportsSyntaxTree)
                symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, caretPosition.Position);
            else
                symbol = await FindSymbolFromXamlCursorAsync(caretPosition, workspace);

            return (wpfTextView, symbol);
        }
    }
}
