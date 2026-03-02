using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace XamlNav
{
    /// <summary>
    /// Centralized logging utility for debug output to the Visual Studio Output window.
    /// Call <see cref="Initialize"/> once during package startup; afterwards call
    /// <see cref="Log"/>, <see cref="LogAsync"/>, or <see cref="LogSync"/> without a service provider.
    /// All methods are safe no-ops when not initialized (e.g. in unit tests).
    /// </summary>
    internal static class DebugLogger
    {
        /// <summary>GUID for the debug output pane (shared with package initialization).</summary>
        public const string PaneGuidString = "0199d16b-6f5a-4650-8870-5e339b9db5e6";
        /// <summary>Display name for the debug output pane.</summary>
        public const string PaneName = "XamlNav Debug";

        private static IVsOutputWindowPane _debugPane;
        private static IServiceProvider _serviceProvider;
        private static readonly object _lock = new object();

        /// <summary>
        /// Returns true if the logger has been initialized with a service provider.
        /// </summary>
        public static bool IsInitialized => _serviceProvider != null;

        /// <summary>
        /// Initializes the logger with the package's service provider.
        /// Must be called once on the UI thread during package startup.
        /// </summary>
        public static void Initialize(IServiceProvider serviceProvider)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

            // Eagerly create the output pane so it's ready for first log call
            EnsurePane();
        }

        private static void EnsurePane()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_debugPane != null || _serviceProvider == null)
                return;

            lock (_lock)
            {
                if (_debugPane != null) return;

                var outputWindow = _serviceProvider.GetService(typeof(SVsOutputWindow)) as IVsOutputWindow;
                if (outputWindow != null)
                {
                    Guid paneGuid = new Guid(PaneGuidString);
                    outputWindow.CreatePane(ref paneGuid, PaneName, 1, 1);
                    outputWindow.GetPane(ref paneGuid, out _debugPane);
                }
            }
        }

        /// <summary>
        /// Logs a message to the debug output pane. Must be called from the UI thread.
        /// No-op if not initialized.
        /// </summary>
        public static void Log(string message)
        {
            if (!IsInitialized) return;
            ThreadHelper.ThrowIfNotOnUIThread();
            LogCore(message);
        }

        /// <summary>
        /// Logs a message asynchronously, switching to the UI thread first.
        /// No-op if not initialized.
        /// </summary>
        public static async Task LogAsync(string message)
        {
            if (!IsInitialized) return;
            await LogAsyncCoreAsync(message);
        }

        /// <summary>
        /// Logs a message synchronously from any thread by blocking until the UI thread is available.
        /// No-op if not initialized.
        /// </summary>
        public static void LogSync(string message)
        {
            if (!IsInitialized) return;
            LogSyncCore(message);
        }

        // ----- Core implementations behind separate methods to avoid JIT loading VS Shell types -----

        private static void LogCore(string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EnsurePane();
            _debugPane?.OutputStringThreadSafe($"{DateTime.Now:HH:mm:ss}: {message}\n");
        }

        private static async Task LogAsyncCoreAsync(string message)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            LogCore(message);
        }

        private static void LogSyncCore(string message)
        {
            ThreadHelper.JoinableTaskFactory.Run(() => LogAsyncCoreAsync(message));
        }
    }
}
