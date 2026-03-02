using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace XamlNav
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideAutoLoad(UIContextGuids80.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideOptionPage(typeof(XamlNavigationOptionsPage),
        "XamlNav", "General", 0, 0, true)]
    [ProvideProfile(typeof(XamlNavigationOptionsPage),
        "XamlNav", "General", 0, 0, true)]
    public sealed class XamlNavPackage : AsyncPackage
    {
        public const string PackageGuidString = "fe683998-c5bb-4103-b29f-a150ce0a6356";

        /// <summary>Singleton instance set during initialization.</summary>
        public static XamlNavPackage Instance { get; private set; }

        /// <summary>
        /// Retrieves the options page from any context that has access to an IServiceProvider.
        /// Falls back to the static Instance when the service provider cannot resolve the package.
        /// </summary>
        public static XamlNavigationOptionsPage GetOptions()
        {
            return Instance?.GetDialogPage(typeof(XamlNavigationOptionsPage)) as XamlNavigationOptionsPage;
        }

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            Instance = this;

            // Initialize centralized logger with this package's service provider
            DebugLogger.Initialize(this);
            await DebugLogger.LogAsync("Extension Package Initialized!");

            // Initialize commands
            await RestartDesignerCommand.InitializeAsync(this);
            await FindXamlReferencesCommand.InitializeAsync(this);
            await RenameXamlReferencesCommand.InitializeAsync(this);
            await OpenSettingsCommand.InitializeAsync(this);
        }
    }
}

