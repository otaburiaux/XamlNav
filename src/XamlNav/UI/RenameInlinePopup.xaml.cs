using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace XamlNav
{
    /// <summary>
    /// VS-style inline rename popup implemented as a modal borderless Window.
    /// Colors adapt to the active VS theme (Light, Dark, Blue) via VSColorTheme API.
    /// Usage:
    ///   var popup = new RenameInlinePopup(currentName, position, previewProvider);
    ///   if (popup.ShowDialog() == true) { ... popup.NewName ... }
    /// </summary>
    public partial class RenameInlinePopup : Window
    {
        private readonly Func<string, IEnumerable<string>> _previewProvider;
        private bool _previewVisible;
        private bool _explicitClose; // Guard so Deactivated doesn't set DialogResult after we already did

        public string NewName { get; private set; }

        public RenameInlinePopup(string currentName, Point position, Func<string, IEnumerable<string>> previewProvider = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            InitializeComponent();
            ApplyVsThemeColors();
            _previewProvider = previewProvider;
            NewName = currentName;
            NameTextBox.Text = currentName;

            // Set owner to VS main window so we behave correctly with z-order
            try
            {
                var shell = ServiceProvider.GlobalProvider.GetService(typeof(SVsUIShell)) as IVsUIShell;
                if (shell != null && shell.GetDialogOwnerHwnd(out IntPtr hwndOwner) == VSConstants.S_OK)
                {
                    new System.Windows.Interop.WindowInteropHelper(this).Owner = hwndOwner;
                }
            }
            catch { /* Fallback if shell is unavailable */ }

            Left = position.X;
            Top = position.Y;

            Loaded += (s, e) =>
            {
                NameTextBox.Focus();
                NameTextBox.SelectAll();
                Activate();
            };
        }

        /// <summary>
        /// Reads the active VS theme colors and injects them as local resource brushes.
        /// XAML elements reference these via DynamicResource (e.g. {DynamicResource PopupBg}).
        /// </summary>
        private void ApplyVsThemeColors()
        {
            try
            {
                SetBrush("PopupBg", EnvironmentColors.ToolWindowBackgroundColorKey);
                SetBrush("PopupBorder", EnvironmentColors.PanelHyperlinkColorKey);
                SetBrush("PopupText", EnvironmentColors.ToolWindowTextColorKey);
                SetBrush("InputBg", EnvironmentColors.ComboBoxBackgroundColorKey);
                SetBrush("InputBorder", EnvironmentColors.ComboBoxBorderColorKey);
                SetBrush("InputAccent", EnvironmentColors.PanelHyperlinkColorKey);
                SetBrush("HoverBg", EnvironmentColors.CommandBarMouseOverBackgroundBeginColorKey);
                SetBrush("GrayText", EnvironmentColors.SystemGrayTextColorKey);
            }
            catch { /* Fallback to XAML defaults if theming API unavailable */ }
        }

        private void SetBrush(string resourceKey, ThemeResourceKey colorKey)
        {
            var c = VSColorTheme.GetThemedColor(colorKey);
            Resources[resourceKey] = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
        }

        private void NameTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                    TogglePreview();
                else if (!string.IsNullOrWhiteSpace(NameTextBox.Text))
                    Apply();

                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Cancel();
                e.Handled = true;
            }
        }

        private void NameTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyButton.IsEnabled = !string.IsNullOrWhiteSpace(NameTextBox.Text);
            if (_previewVisible) RefreshPreview();
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(NameTextBox.Text))
                Apply();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Cancel();

        private void Apply()
        {
            _explicitClose = true;
            NewName = NameTextBox.Text.Trim();
            DialogResult = true;
            Close();
        }

        private void Cancel()
        {
            _explicitClose = true;
            DialogResult = false;
            Close();
        }

        private void TogglePreview()
        {
            _previewVisible = !_previewVisible;
            if (_previewVisible)
            {
                RefreshPreview();
                PreviewPanel.Visibility = Visibility.Visible;
                HintLabel.Text = "↵ apply  ·  Shift+↵ hide preview  ·  Esc cancel";
            }
            else
            {
                PreviewPanel.Visibility = Visibility.Collapsed;
                HintLabel.Text = "↵ apply  ·  Shift+↵ preview  ·  Esc cancel";
            }
        }

        private void RefreshPreview()
        {
            if (_previewProvider == null) return;
            string typed = NameTextBox.Text.Trim();
            var lines = new List<string>(_previewProvider(typed));
            PreviewHeader.Text = $"Changes ({lines.Count}):";
            PreviewList.ItemsSource = lines;
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            // If the user clicks away (loses focus) and we haven't explicitly closed,
            // treat it as a Cancel (light dismiss).
            if (!_explicitClose && IsLoaded)
            {
                try
                {
                    _explicitClose = true;
                    DialogResult = false;
                    Close();
                }
                catch { /* ignore redundant close errors */ }
            }
        }
    }
}
