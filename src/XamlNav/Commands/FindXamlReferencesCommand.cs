using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.FindAllReferences;
using Microsoft.VisualStudio.Shell.TableControl;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Task = System.Threading.Tasks.Task;

namespace XamlNav
{
    /// <summary>
    /// Command to find all references to a symbol, including XAML bindings.
    /// Results are displayed in the standard VS Find All References window.
    /// Works from both C# and XAML editors.
    /// </summary>
    internal sealed class FindXamlReferencesCommand
    {
        public const int CommandId = 0x0200;
        public static readonly Guid CommandSet = new Guid("85e5d978-f416-4516-bb76-d473f6f43025");

        private readonly AsyncPackage _package;
        private readonly VisualStudioWorkspace _workspace;
        private readonly XamlReferenceFinder _xamlFinder;
        private readonly IClassificationFormatMap _formatMap;
        private readonly IClassificationTypeRegistryService _typeRegistry;

        private FindXamlReferencesCommand(
            AsyncPackage package,
            OleMenuCommandService commandService,
            VisualStudioWorkspace workspace,
            XamlReferenceFinder xamlFinder,
            IClassificationFormatMap formatMap,
            IClassificationTypeRegistryService typeRegistry)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            _workspace = workspace;
            _xamlFinder = xamlFinder;
            _formatMap = formatMap;
            _typeRegistry = typeRegistry;

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(Execute, menuCommandID);
            commandService.AddCommand(menuItem);
        }

        public static FindXamlReferencesCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            var componentModel = await package.GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
            if (commandService == null || componentModel == null) return;
            var workspace = componentModel.GetService<VisualStudioWorkspace>();
            var xamlFinder = componentModel.GetService<XamlReferenceFinder>();
            var formatMapService = componentModel.GetService<IClassificationFormatMapService>();
            var typeRegistry = componentModel.GetService<IClassificationTypeRegistryService>();
            var formatMap = formatMapService.GetClassificationFormatMap("text");

            Instance = new FindXamlReferencesCommand(package, commandService, workspace, xamlFinder, formatMap, typeRegistry);
        }

        private void Execute(object sender, EventArgs e)
        {
            _ = _package.JoinableTaskFactory.RunAsync(() => ExecuteAsync());
        }

        /// <summary>
        /// Entry point for the mouse processor: invokes Find All References from a known XAML buffer position.
        /// </summary>
        public void ExecuteFromPosition(IWpfTextView view, SnapshotPoint position)
        {
            _ = _package.JoinableTaskFactory.RunAsync(() => ExecuteFromPositionAsync(view, position));
        }

        /// <summary>
        /// Entry point for the C# mouse processor: resolves the symbol via Roslyn and finds all references including XAML.
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
                    await DebugLogger.LogAsync("[FindXamlReferences] No Roslyn document at C# mouse position.");
                    return;
                }

                var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position.Position);
                if (symbol == null)
                {
                    await DebugLogger.LogAsync("[FindXamlReferences] No symbol found at C# mouse position.");
                    return;
                }

                await ExecuteCoreAsync(symbol);
            }
            catch (Exception ex)
            {
                await DebugLogger.LogAsync($"[FindXamlReferences] C# exception: {ex.Message}");
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
                    await DebugLogger.LogAsync("[FindXamlReferences] No symbol found at mouse position.");
                    return;
                }

                await ExecuteCoreAsync(symbol);
            }
            catch (Exception ex)
            {
                await DebugLogger.LogAsync($"[FindXamlReferences] Exception: {ex.Message}");
            }
        }

        private async Task ExecuteAsync()
        {
            try
            {
                var (wpfTextView, symbol) = await XamlNavigationHelper.ResolveActiveViewAndSymbolAsync(_package, _workspace);

                if (symbol == null)
                {
                    await DebugLogger.LogAsync("[FindXamlReferences] No symbol found at cursor position.");
                    return;
                }

                await ExecuteCoreAsync(symbol);
            }
            catch (Exception ex)
            {
                await DebugLogger.LogAsync($"[FindXamlReferences] Exception: {ex.Message}");
            }
        }

        private async Task ExecuteCoreAsync(ISymbol symbol)
        {
            await DebugLogger.LogAsync($"[FindXamlReferences] Searching references for symbol: '{symbol.Name}' ({symbol.Kind})");

            // 1. Open the standard Find All References window
            var findAllRefsService = await _package.GetServiceAsync(typeof(SVsFindAllReferences)) as IFindAllReferencesService;
            if (findAllRefsService == null) return;
            var farWindow = findAllRefsService.StartSearch($"References to '{symbol.Name}' (with XAML)");

            // 2. Create our data source and register it with the FAR window
            var dataSource = new XamlReferenceTableDataSource();
            farWindow.Manager.AddSource(dataSource);

            // 3. Run both Roslyn and XAML searches in parallel
            var codeReferencesTask = SymbolFinder.FindReferencesAsync(symbol, _workspace.CurrentSolution);
            var xamlReferencesTask = _xamlFinder.FindReferencesAsync(symbol);

            await Task.WhenAll(codeReferencesTask, xamlReferencesTask);

            var codeReferences = await codeReferencesTask;
            var xamlReferences = await xamlReferencesTask;

            // 4. Map results to entries
            var entries = new List<XamlReferenceEntry>();

            // C# code references
            foreach (var group in codeReferences)
            {
                string projectName = group.Definition.ContainingAssembly?.Name ?? string.Empty;
                foreach (var refLoc in group.Locations)
                {
                    entries.Add(new XamlReferenceEntry(refLoc, projectName, _formatMap, _typeRegistry));
                }
            }

            // XAML binding references (filtered by ShowUnverifiedBindings setting)
            var options = XamlNavPackage.GetOptions();
            bool showUnverified = options?.ShowUnverifiedBindings ?? true;

            foreach (var xamlRef in xamlReferences)
            {
                if (!xamlRef.IsVerified && !showUnverified)
                    continue;

                string projectLabel = xamlRef.IsVerified ? "XAML" : "XAML (unverified)";
                entries.Add(new XamlReferenceEntry(xamlRef.Location, projectLabel, xamlRef.IsVerified, _formatMap, _typeRegistry));
            }

            // 5. Push entries to the FAR window and mark complete
            dataSource.AddEntries(entries);
            dataSource.Complete();

            await DebugLogger.LogAsync($"[FindXamlReferences] Done. Found {entries.Count} references ({xamlReferences.Count()} XAML).");
        }
    }
}
