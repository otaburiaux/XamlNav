using System;
using System.ComponentModel.Design;
using System.Diagnostics;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace XamlNav
{
    /// <summary>
    /// Command to restart the XAML Designer by killing its process.
    /// </summary>
    internal sealed class RestartDesignerCommand
    {
        /// <summary>
        /// Command ID - must match VSCT file.
        /// </summary>
        public const int CommandId = 0x0100;
        public const int ContextCommandId = 0x0101;

        /// <summary>
        /// Command menu group (command set GUID) - must match VSCT file.
        /// </summary>
        public static readonly Guid CommandSet = new Guid("85e5d978-f416-4516-bb76-d473f6f43025");

        /// <summary>
        /// VS Package that provides this command.
        /// </summary>
        private readonly AsyncPackage _package;


        /// <summary>
        /// Process names for XAML Designer across different VS versions.
        /// </summary>
        private static readonly string[] DesignerProcessNames = new[]
        {
            "WpfSurface",      // VS 2022+, VS 2019 new WPF designer
            "XDesProc",        // VS 2019 old designer
            "UwpSurface"       // UWP designer
        };

        private RestartDesignerCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            // Register main menu command
            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(Execute, menuCommandID);
            commandService.AddCommand(menuItem);

            // Register context menu command
            var contextCommandID = new CommandID(CommandSet, ContextCommandId);
            var contextMenuItem = new MenuCommand(Execute, contextCommandID);
            commandService.AddCommand(contextMenuItem);
        }

        /// <summary>
        /// Gets the singleton instance.
        /// </summary>
        public static RestartDesignerCommand Instance { get; private set; }

        /// <summary>
        /// Initializes the command.
        /// </summary>
        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new RestartDesignerCommand(package, commandService);
        }

        /// <summary>
        /// Executes the command - kills all XAML Designer processes.
        /// </summary>
        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            int killedCount = 0;

            foreach (var processName in DesignerProcessNames)
            {
                try
                {
                    var processes = Process.GetProcessesByName(processName);
                    foreach (var process in processes)
                    {
                        try
                        {
                            process.Kill();
                            killedCount++;
                            DebugLogger.Log($"[RestartDesigner] Killed {processName} (PID: {process.Id})");
                        }
                        catch (Exception ex)
                        {
                            DebugLogger.Log($"[RestartDesigner] Failed to kill {processName}: {ex.Message}");
                        }
                        finally
                        {
                            process.Dispose();
                        }
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"[RestartDesigner] Error getting processes: {ex.Message}");
                }
            }

            // Show message if no processes were found
            if (killedCount == 0)
            {
                VsShellUtilities.ShowMessageBox(
                    _package,
                    "No XAML Designer processes found to restart.",
                    "Restart XAML Designer",
                    Microsoft.VisualStudio.Shell.Interop.OLEMSGICON.OLEMSGICON_INFO,
                    Microsoft.VisualStudio.Shell.Interop.OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    Microsoft.VisualStudio.Shell.Interop.OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
            else
            {
                DebugLogger.Log($"[RestartDesigner] Killed {killedCount} designer process(es)");
            }
        }
    }
}
