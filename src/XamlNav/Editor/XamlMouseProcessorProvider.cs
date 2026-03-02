using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace XamlNav
{
    /// <summary>
    /// MEF provider that creates <see cref="XamlMouseProcessor"/> instances for XAML text views.
    /// Enables Ctrl+Click, Ctrl+Shift+Click, and Ctrl+Alt+Click navigation in XAML editors.
    /// </summary>
    [Export(typeof(IMouseProcessorProvider))]
    [Name("XamlGoToDefinitionMouseProcessorProvider")]
    [ContentType("XAML")] // Only execute for XAML files
    [Order(Before = "WordSelection")] // Run before VS's built-in word-selection handler so Ctrl+Click navigates instead of selecting
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    public class XamlMouseProcessorProvider : IMouseProcessorProvider
    {
        public IMouseProcessor GetAssociatedProcessor(IWpfTextView wpfTextView)
        {
            return new XamlMouseProcessor(wpfTextView);
        }
    }
}
