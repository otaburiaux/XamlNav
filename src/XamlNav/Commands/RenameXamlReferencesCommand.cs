using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Windows;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
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
    /// Command that renames a C# symbol via Roslyn AND updates all XAML binding
    /// references to that property name across the solution.
    ///
    /// XAML edits are applied through the VS text buffer API so that:
    ///   - Modified files get the dirty (*) marker and are savable normally.
    ///   - All edits (C# + XAML) are wrapped in a linked undo transaction,
    ///     so a single Ctrl+Z from the originating file undoes everything.
    ///
    /// Works from both C# and XAML editors.
    /// </summary>
    internal sealed class RenameXamlReferencesCommand
    {
        public const int CommandId = 0x0300;
        public static readonly Guid CommandSet = new Guid("85e5d978-f416-4516-bb76-d473f6f43025");

        private readonly AsyncPackage _package;
        private readonly VisualStudioWorkspace _workspace;
        private readonly XamlDataContextResolver _resolver = new XamlDataContextResolver();

        private RenameXamlReferencesCommand(
            AsyncPackage package,
            OleMenuCommandService commandService,
            VisualStudioWorkspace workspace)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            _workspace = workspace;

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(Execute, menuCommandID);
            commandService.AddCommand(menuItem);
        }

        public static RenameXamlReferencesCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            var componentModel = await package.GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
            if (commandService == null || componentModel == null) return;
            var workspace = componentModel.GetService<VisualStudioWorkspace>();

            Instance = new RenameXamlReferencesCommand(package, commandService, workspace);
        }

        private void Execute(object sender, EventArgs e)
        {
            _ = _package.JoinableTaskFactory.RunAsync(() => ExecuteAsync());
        }

        /// <summary>
        /// Entry point for the mouse processor: invokes Rename from a known XAML buffer position.
        /// </summary>
        public void ExecuteFromPosition(IWpfTextView view, SnapshotPoint position)
        {
            _ = _package.JoinableTaskFactory.RunAsync(() => ExecuteFromPositionAsync(view, position));
        }

        /// <summary>
        /// Entry point for the C# mouse processor: resolves the symbol via Roslyn and renames including XAML references.
        /// </summary>
        public void ExecuteFromCSharpPosition(IWpfTextView view, SnapshotPoint position)
        {
            _ = _package.JoinableTaskFactory.RunAsync(() => ExecuteFromCSharpPositionAsync(view, position));
        }

        private async Task ExecuteFromCSharpPositionAsync(IWpfTextView view, SnapshotPoint position)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                Document document = position.Snapshot.GetOpenDocumentInCurrentContextWithChanges();
                if (document == null || !document.SupportsSyntaxTree)
                {
                    await DebugLogger.LogAsync("[RenameXaml] No Roslyn document at C# mouse position.");
                    return;
                }

                var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position.Position);
                if (symbol == null)
                {
                    await DebugLogger.LogAsync("[RenameXaml] No symbol found at C# mouse position.");
                    return;
                }

                await ExecuteCoreAsync(view, symbol);
            }
            catch (Exception ex)
            {
                await DebugLogger.LogAsync($"[RenameXaml] C# exception: {ex.Message}");
            }
        }

        private async Task ExecuteFromPositionAsync(IWpfTextView view, SnapshotPoint position)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var symbol = await XamlNavigationHelper.FindSymbolFromXamlCursorAsync(position, _workspace);
                if (symbol == null)
                {
                    await DebugLogger.LogAsync("[RenameXaml] No symbol found at mouse position.");
                    return;
                }

                await ExecuteCoreAsync(view, symbol);
            }
            catch (Exception ex)
            {
                await DebugLogger.LogAsync($"[RenameXaml] Exception: {ex.Message}");
            }
        }

        private async Task ExecuteAsync()
        {
            try
            {
                var (wpfTextView, symbol) = await XamlNavigationHelper.ResolveActiveViewAndSymbolAsync(_package, _workspace);

                if (symbol == null)
                {
                    await DebugLogger.LogAsync("[RenameXaml] No symbol found at cursor.");
                    return;
                }

                await ExecuteCoreAsync(wpfTextView, symbol);
            }
            catch (Exception ex)
            {
                await DebugLogger.LogAsync($"[RenameXaml] Exception: {ex.Message}");
            }
        }

        private async Task ExecuteCoreAsync(IWpfTextView wpfTextView, ISymbol symbol)
        {
            string oldName = symbol.Name;
            await DebugLogger.LogAsync($"[RenameXaml] Symbol: '{oldName}' ({symbol.Kind})");

            var property = symbol as IPropertySymbol;

            // Build preview lines
            IEnumerable<string> PreviewProvider(string typedName)
            {
                var previewEdits = ThreadHelper.JoinableTaskFactory.Run(() => CollectXamlEditsAsync(property, oldName, typedName));
                var lines = new List<string>();
                lines.Add($"  • {symbol.ContainingType?.Name ?? symbol.ContainingNamespace?.Name}.cs  :  {oldName} → {typedName}");
                foreach (var (filePath, _) in previewEdits)
                    lines.Add($"  • {System.IO.Path.GetFileName(filePath)}  :  {oldName} → {typedName}");
                return lines;
            }

            // Calculate position for inline popup (must be on UI thread).
            // Caret.Top is in text-surface coordinates; subtract ViewportTop
            // to get the position relative to the editor's visual element.
            var editorElement = wpfTextView.VisualElement;
            double caretViewportY = wpfTextView.Caret.Top - wpfTextView.ViewportTop + wpfTextView.LineHeight;
            var screenPoint = editorElement.PointToScreen(
                new Point(wpfTextView.Caret.Left - wpfTextView.ViewportLeft, caretViewportY));

            var popup = new RenameInlinePopup(oldName, screenPoint, PreviewProvider);
            bool? result = popup.ShowDialog();

            if (result != true || string.IsNullOrWhiteSpace(popup.NewName) || popup.NewName == oldName)
            {
                await DebugLogger.LogAsync("[RenameXaml] Rename cancelled or name unchanged.");
                return;
            }

            string newName = popup.NewName;

            await DebugLogger.LogAsync($"[RenameXaml] Renaming '{oldName}' → '{newName}'");

            // Collect XAML files that need changes (before Roslyn rename alters the workspace)
            var xamlEdits = await CollectXamlEditsAsync(property, oldName, newName);

            // Open a linked undo transaction so ALL edits (C# + XAML) undo as one step
            var linkedUndoMgr = await _package.GetServiceAsync(typeof(SVsLinkedUndoTransactionManager))
                as IVsLinkedUndoTransactionManager;

            bool linkedUndoOpened = false;
            if (linkedUndoMgr != null)
            {
                int hr = linkedUndoMgr.OpenLinkedUndo(
                    (uint)LinkedTransactionFlags2.mdtGlobal,
                    $"Rename '{oldName}' to '{newName}'");
                linkedUndoOpened = ErrorHandler.Succeeded(hr);
            }

            try
            {
                // Roslyn rename (C# code) — participates in VS undo automatically
                await RenameInCodeAsync(symbol, newName);

                // Apply XAML edits through VS text buffers (dirty marker + undo)
                int xamlFilesChanged = await ApplyXamlEditsViaBuffersAsync(xamlEdits);

                await DebugLogger.LogAsync($"[RenameXaml] Done. Updated {xamlFilesChanged} XAML file(s).");

                if (linkedUndoOpened)
                    linkedUndoMgr.CloseLinkedUndo();
            }
            catch
            {
                if (linkedUndoOpened)
                    linkedUndoMgr.AbortLinkedUndo();
                throw;
            }
        }

        // -------------------------------------------------------------------------
        // Roslyn rename
        // -------------------------------------------------------------------------

        private async Task RenameInCodeAsync(ISymbol symbol, string newName)
        {
            var options = new SymbolRenameOptions();
            var newSolution = await Renamer.RenameSymbolAsync(
                _workspace.CurrentSolution, symbol, options, newName);

            bool applied = _workspace.TryApplyChanges(newSolution);
            DebugLogger.LogSync($"[RenameXaml] TryApplyChanges: {applied}");
        }

        // -------------------------------------------------------------------------
        // XAML edit collection (pure, no IO side-effects)
        // -------------------------------------------------------------------------

        /// <summary>
        /// Scans all XAML files and returns a list of (filePath, newContent) pairs
        /// for files that actually need changes. Uses DataContext resolution to filter
        /// out bindings whose DataContext is definitely incompatible with the property's
        /// containing type. Unverified bindings (DataContext unknown) are still included.
        /// </summary>
        private async Task<List<(string FilePath, string NewContent)>> CollectXamlEditsAsync(IPropertySymbol property, string oldName, string newName)
        {
            var results = new List<(string, string)>();
            var processedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var project in _workspace.CurrentSolution.Projects)
            {
                if (string.IsNullOrEmpty(project.FilePath)) continue;

                string projectDir = Path.GetDirectoryName(project.FilePath);
                if (!Directory.Exists(projectDir)) continue;

                IEnumerable<string> xamlFiles;
                try
                {
                    xamlFiles = Directory.EnumerateFiles(projectDir, "*.xaml", SearchOption.AllDirectories)
                        .Where(f => !XamlNavigationHelper.ShouldSkipFile(f));
                }
                catch { continue; }

                foreach (var filePath in xamlFiles)
                {
                    if (!processedFiles.Add(filePath)) continue;

                    try
                    {
                        string content = File.ReadAllText(filePath);
                        string updated = await ReplaceFilteredBindingsAsync(content, filePath, property, oldName, newName);
                        if (updated != content)
                            results.Add((filePath, updated));
                    }
                    catch { /* skip unreadable files */ }
                }
            }

            return results;
        }

        // -------------------------------------------------------------------------
        // XAML buffer-based apply (dirty marker + undo)
        // -------------------------------------------------------------------------

        /// <summary>
        /// Applies pre-computed XAML content changes through the VS text buffer API.
        /// Each file is opened via IVsInvisibleEditorManager so it gets the dirty (*)
        /// marker and its edits participate in the linked undo transaction.
        /// </summary>
        private async Task<int> ApplyXamlEditsViaBuffersAsync(List<(string FilePath, string NewContent)> edits)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (edits.Count == 0) return 0;

            var invisibleEditorMgr = await _package.GetServiceAsync(typeof(SVsInvisibleEditorManager))
                as IVsInvisibleEditorManager;
            if (invisibleEditorMgr == null)
            {
                DebugLogger.LogSync("[RenameXaml] IVsInvisibleEditorManager unavailable, falling back to File.WriteAllText.");
                return FallbackWriteAllText(edits);
            }

            int filesChanged = 0;

            foreach (var (filePath, newContent) in edits)
            {
                try
                {
                    // Open the file into an invisible editor (or reuse the existing buffer if open)
                    int hr = invisibleEditorMgr.RegisterInvisibleEditor(
                        filePath,
                        pProject: null,
                        dwFlags: (uint)_EDITORREGFLAGS.RIEF_ENABLECACHING,
                        pFactory: null,
                        out IVsInvisibleEditor invisibleEditor);

                    if (ErrorHandler.Failed(hr) || invisibleEditor == null) continue;

                    // Get the IVsTextLines buffer from the invisible editor
                    var bufferGuid = typeof(IVsTextLines).GUID;
                    hr = invisibleEditor.GetDocData(
                        fEnsureWritable: 1,
                        riid: ref bufferGuid,
                        ppDocData: out IntPtr docDataPtr);

                    if (ErrorHandler.Failed(hr) || docDataPtr == IntPtr.Zero) continue;

                    IVsTextLines textLines = null;
                    try
                    {
                        textLines = Marshal.GetObjectForIUnknown(docDataPtr) as IVsTextLines;
                    }
                    finally
                    {
                        Marshal.Release(docDataPtr);
                    }

                    if (textLines == null) continue;

                    // Replace the entire buffer content with the updated XAML.
                    // ReplaceLines takes pszText as IntPtr (COM interop), so we marshal the string.
                    textLines.GetLastLineIndex(out int lastLine, out int lastCol);

                    var changedSpan = new Microsoft.VisualStudio.TextManager.Interop.TextSpan[1];
                    IntPtr pszText = Marshal.StringToHGlobalUni(newContent);
                    try
                    {
                        hr = textLines.ReplaceLines(
                            iStartLine: 0,
                            iStartIndex: 0,
                            iEndLine: lastLine,
                            iEndIndex: lastCol,
                            pszText: pszText,
                            iNewLen: newContent.Length,
                            pChangedSpan: changedSpan);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(pszText);
                    }

                    if (ErrorHandler.Succeeded(hr))
                    {
                        filesChanged++;
                        await DebugLogger.LogAsync($"[RenameXaml] Buffer-updated XAML: {Path.GetFileName(filePath)}");
                    }
                }
                catch (Exception ex)
                {
                    await DebugLogger.LogAsync($"[RenameXaml] Buffer edit failed for {filePath}: {ex.Message}");
                }
            }

            return filesChanged;
        }

        /// <summary>
        /// Last-resort fallback if the invisible editor service is unavailable.
        /// Does NOT produce dirty markers or undo entries.
        /// </summary>
        private int FallbackWriteAllText(List<(string FilePath, string NewContent)> edits)
        {
            int count = 0;
            foreach (var (filePath, newContent) in edits)
            {
                try { File.WriteAllText(filePath, newContent); count++; }
                catch { /* ignore */ }
            }
            return count;
        }

        // -------------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------------

        /// <summary>
        /// Replaces binding property names in XAML content, but only for bindings
        /// whose DataContext is compatible with (or unknown for) the property's containing type.
        /// Bindings whose DataContext resolves to a definitely-incompatible type are skipped.
        /// </summary>
        private async Task<string> ReplaceFilteredBindingsAsync(string content, string filePath, IPropertySymbol property, string oldName, string newName)
        {
            var bindings = XamlBindingParser.ParseBindings(content);
            var options = XamlNavPackage.GetOptions();
            bool renameUnverified = options?.RenameUnverifiedBindings ?? false;

            // Collect positions that should be renamed (filter by DataContext compatibility)
            var edits = new List<(int Start, int Length, string NewPath)>();

            foreach (var binding in bindings)
            {
                if (!XamlReferenceFinder.IsMatch(binding.PropertyName, oldName))
                    continue;

                // Check DataContext compatibility if we have a property symbol
                if (property != null)
                {
                    var resolved = _resolver.Resolve(content, binding.BindingExpressionStart, filePath);

                    if (resolved != null && resolved.FullTypeName != null)
                    {
                        bool? compatible = await XamlReferenceFinder.IsResolvedTypeCompatibleAsync(
                                resolved.FullTypeName, property.ContainingType, _workspace);

                        if (compatible == false)
                            continue; // Definitely incompatible → skip

                        if (compatible == null && !renameUnverified)
                            continue; // Unverified + setting says skip
                    }
                    else if (!renameUnverified)
                    {
                        continue; // No DataContext resolved + setting says skip unverified
                    }
                }

                string newPath = XamlBindingParser.ReplaceSegmentInPath(binding.PropertyName, oldName, newName);
                if (newPath != binding.PropertyName)
                {
                    edits.Add((binding.StartPosition, binding.Length, newPath));
                }
            }

            if (edits.Count == 0)
                return content;

            // Apply edits from end to start so positions remain valid
            edits.Sort((a, b) => b.Start.CompareTo(a.Start));

            var sb = new System.Text.StringBuilder(content);
            foreach (var (start, length, newPath) in edits)
            {
                sb.Remove(start, length);
                sb.Insert(start, newPath);
            }

            return sb.ToString();
        }
    }
}
