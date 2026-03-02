using System;
using System.Windows.Input;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;

namespace XamlNav
{
    /// <summary>
    /// Handles mouse events in C# text views. Only Ctrl+Shift+Click is intercepted
    /// (for Find All References with XAML); other combos are left to VS's built-in
    /// Go To Definition. The Ctrl+Shift+Click action is configurable via
    /// Tools > Options > XamlNav > General.
    /// </summary>
    public class CSharpMouseProcessor : MouseProcessorBase
    {
        private readonly IWpfTextView _view;

        public CSharpMouseProcessor(IWpfTextView view)
        {
            _view = view ?? throw new ArgumentNullException(nameof(view));

            _view.VisualElement.PreviewKeyDown += OnKeyDown;
            _view.VisualElement.PreviewKeyUp += OnKeyUp;
            _view.VisualElement.LostKeyboardFocus += OnLostFocus;
            _view.Closed += OnViewClosed;
        }

        private void OnViewClosed(object sender, EventArgs e)
        {
            _view.VisualElement.PreviewKeyDown -= OnKeyDown;
            _view.VisualElement.PreviewKeyUp -= OnKeyUp;
            _view.VisualElement.LostKeyboardFocus -= OnLostFocus;
            _view.Closed -= OnViewClosed;
            Mouse.OverrideCursor = null;
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl ||
                e.Key == Key.LeftShift || e.Key == Key.RightShift ||
                e.Key == Key.LeftAlt || e.Key == Key.RightAlt)
            {
                bool hasAction = GetActionForCurrentModifiers() != CtrlClickAction.None;
                if (hasAction)
                {
                    var mousePos = Mouse.GetPosition(_view.VisualElement);
                    UpdateCursorForPosition(mousePos);
                }
            }
        }

        private void OnKeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl ||
                e.Key == Key.LeftShift || e.Key == Key.RightShift ||
                e.Key == Key.LeftAlt || e.Key == Key.RightAlt)
            {
                if (GetActionForCurrentModifiers() == CtrlClickAction.None)
                    Mouse.OverrideCursor = null;
            }
        }

        private void OnLostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            Mouse.OverrideCursor = null;
        }

        public override void PreprocessMouseMove(MouseEventArgs e)
        {
            if (GetActionForCurrentModifiers() != CtrlClickAction.None)
            {
                var position = e.GetPosition(_view.VisualElement);
                UpdateCursorForPosition(position);
            }
            else if (Mouse.OverrideCursor != null)
            {
                Mouse.OverrideCursor = null;
            }
        }

        public override void PreprocessMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                var action = GetActionForCurrentModifiers();
                if (action == CtrlClickAction.None)
                    return;

                DebugLogger.LogSync($"C# {action} click detected.");

                var position = e.GetPosition(_view.VisualElement);
                var bufferPosition = GetBufferPositionFromMousePosition(position);

                if (!bufferPosition.HasValue)
                {
                    DebugLogger.LogSync("Could not map mouse position to buffer position.");
                    return;
                }

                if (!IsOnIdentifier(bufferPosition.Value))
                {
                    DebugLogger.LogSync("Click was not on an identifier.");
                    return;
                }

                switch (action)
                {
                    case CtrlClickAction.GoToDefinition:
                        XamlNavigationHelper.HighlightAndNavigate(_view, bufferPosition.Value);
                        break;

                    case CtrlClickAction.FindAllReferences:
                        FindXamlReferencesCommand.Instance?.ExecuteFromCSharpPosition(_view, bufferPosition.Value);
                        break;

                    case CtrlClickAction.Rename:
                        RenameXamlReferencesCommand.Instance?.ExecuteFromCSharpPosition(_view, bufferPosition.Value);
                        break;
                }

                e.Handled = true;
                DebugLogger.Log($"C# {action} command executed.");
            }
            catch (Exception ex)
            {
                DebugLogger.LogSync($"Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Determines which action to perform based on current keyboard modifiers and user settings.
        /// </summary>
        private CtrlClickAction GetActionForCurrentModifiers()
        {
            var mods = Keyboard.Modifiers;
            bool ctrl = (mods & ModifierKeys.Control) != 0;
            bool shift = (mods & ModifierKeys.Shift) != 0;
            bool alt = (mods & ModifierKeys.Alt) != 0;

            if (!ctrl) return CtrlClickAction.None;

            // In C# editors, VS natively handles Ctrl+Click, Alt+Click, and
            // Ctrl+Alt+Click for Go To Definition (user-configurable).
            // We only intercept combos that include Shift, which VS never uses.
            if (!shift) return CtrlClickAction.None;

            var options = XamlNavPackage.GetOptions();

            // Ctrl+Shift+Click — safe, no VS conflict
            if (ctrl && shift && !alt)
                return options?.CtrlShiftClickBehavior ?? CtrlClickAction.FindAllReferences;

            return CtrlClickAction.None;
        }

        /// <summary>
        /// Returns true if the buffer position is on a word character (identifier).
        /// </summary>
        private static bool IsOnIdentifier(SnapshotPoint point)
        {
            var snapshot = point.Snapshot;
            if (point.Position >= snapshot.Length)
                return false;

            char c = snapshot[point.Position];
            return char.IsLetterOrDigit(c) || c == '_';
        }

        private void UpdateCursorForPosition(System.Windows.Point screenPosition)
        {
            try
            {
                var bufferPosition = GetBufferPositionFromMousePosition(screenPosition);
                if (bufferPosition.HasValue && IsOnIdentifier(bufferPosition.Value))
                {
                    Mouse.OverrideCursor = Cursors.Hand;
                }
                else
                {
                    Mouse.OverrideCursor = null;
                }
            }
            catch
            {
                Mouse.OverrideCursor = null;
            }
        }

        private SnapshotPoint? GetBufferPositionFromMousePosition(System.Windows.Point position)
        {
            try
            {
                var textViewLines = _view.TextViewLines;
                if (textViewLines == null)
                    return null;

                double textViewY = position.Y + _view.ViewportTop;
                var line = textViewLines.GetTextViewLineContainingYCoordinate(textViewY);
                if (line == null)
                    return null;

                var virtualPoint = line.GetInsertionBufferPositionFromXCoordinate(position.X);
                return virtualPoint.Position;
            }
            catch (Exception ex)
            {
                DebugLogger.LogSync($"[GetBufferPosition] Exception: {ex.Message}");
                return null;
            }
        }
    }
}
