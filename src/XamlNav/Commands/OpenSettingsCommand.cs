using System;
using System.ComponentModel.Design;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace XamlNav
{
    /// <summary>
    /// Command that opens the XamlNav options page (Tools > Options > XamlNav > General).
    /// Wired to Extensions > XamlNav > Settings...
    /// </summary>
    internal sealed class OpenSettingsCommand
    {
        public const int CommandId = 0x0400;
        public static readonly Guid CommandSet = new Guid("85e5d978-f416-4516-bb76-d473f6f43025");

        private readonly AsyncPackage _package;

        public static OpenSettingsCommand Instance { get; private set; }

        private OpenSettingsCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(Execute, menuCommandID);
            commandService.AddCommand(menuItem);
        }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService == null) return;
            Instance = new OpenSettingsCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            _package.ShowOptionPage(typeof(XamlNavigationOptionsPage));
        }
    }
}
