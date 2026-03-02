using System;
using System.Windows.Input;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;

namespace XamlNav
{
    /// <summary>
    /// Handles mouse events in text views to enable configurable modifier+Click actions in XAML.
    /// Actions (GoToDefinition, FindAllReferences, Rename) are assigned to modifier combos
    /// via Tools > Options > XamlNav > General.
    /// </summary>
    public class XamlMouseProcessor : MouseProcessorBase
    {
        private readonly IWpfTextView _view;

        public XamlMouseProcessor(IWpfTextView view)
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
            ThreadHelper.ThrowIfNotOnUIThread();
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

        /// <summary>
        /// Shows hand cursor when a configured modifier combo is held over navigable elements.
        /// </summary>
        public override void PreprocessMouseMove(MouseEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
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

                DebugLogger.LogSync($"{action} click detected. ContentType: '{_view.TextBuffer.ContentType.TypeName}'");

                var position = e.GetPosition(_view.VisualElement);
                var bufferPosition = GetBufferPositionFromMousePosition(position);

                if (!bufferPosition.HasValue)
                {
                    DebugLogger.LogSync("Could not map mouse position to buffer position.");
                    return;
                }

                if (!XamlNavigationHelper.IsNavigablePosition(bufferPosition.Value))
                {
                    DebugLogger.LogSync("Click was NOT in a navigable position.");
                    return;
                }

                switch (action)
                {
                    case CtrlClickAction.GoToDefinition:
                        XamlNavigationHelper.HighlightAndNavigate(_view, bufferPosition.Value);
                        break;

                    case CtrlClickAction.FindAllReferences:
                        FindXamlReferencesCommand.Instance?.ExecuteFromPosition(_view, bufferPosition.Value);
                        break;

                    case CtrlClickAction.Rename:
                        RenameXamlReferencesCommand.Instance?.ExecuteFromPosition(_view, bufferPosition.Value);
                        break;
                }

                e.Handled = true;
                DebugLogger.Log($"{action} command executed.");
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

            var options = XamlNavPackage.GetOptions();

            if (ctrl && shift && !alt)
                return options?.CtrlShiftClickBehavior ?? CtrlClickAction.FindAllReferences;
            if (ctrl && alt && !shift)
                return options?.CtrlAltClickBehavior ?? CtrlClickAction.None;
            if (ctrl && !shift && !alt)
                return options?.CtrlClickBehavior ?? CtrlClickAction.GoToDefinition;

            return CtrlClickAction.None;
        }

        private void UpdateCursorForPosition(System.Windows.Point screenPosition)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var bufferPosition = GetBufferPositionFromMousePosition(screenPosition);
                if (bufferPosition.HasValue)
                {
                    // Reuse existing cursor logic — shows hand when over navigable element
                    XamlNavigationHelper.UpdateCursor(bufferPosition, true);
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
