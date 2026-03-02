using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace XamlNav
{
    /// <summary>
    /// MEF provider that creates <see cref="CSharpMouseProcessor"/> instances for C# text views.
    /// Enables modifier+Click actions (Find All References with XAML, Rename with XAML) in C# editors.
    /// </summary>
    [Export(typeof(IMouseProcessorProvider))]
    [Name("CSharpXamlNavMouseProcessorProvider")]
    [ContentType("CSharp")]
    [Order(Before = "WordSelection")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    public class CSharpMouseProcessorProvider : IMouseProcessorProvider
    {
        public IMouseProcessor GetAssociatedProcessor(IWpfTextView wpfTextView)
        {
            return new CSharpMouseProcessor(wpfTextView);
        }
    }
}
