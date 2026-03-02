using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace XamlNav
{
    /// <summary>
    /// Actions that can be assigned to mouse+modifier combos in XAML editors.
    /// </summary>
    public enum CtrlClickAction
    {
        None,
        GoToDefinition,
        FindAllReferences,
        Rename
    }

    /// <summary>
    /// Options page for "XamlNav" under Tools > Options.
    /// Settings are persisted automatically by VS and roamable via ProvideProfile.
    /// </summary>
    [Guid("282ae53e-223f-4594-a137-8888923c6e8c")]
    public class XamlNavigationOptionsPage : DialogPage
    {
        // --- Unverified bindings ---

        [Category("Find All References")]
        [DisplayName("Show unverified bindings")]
        [Description("Show XAML bindings whose DataContext could not be verified. When false, only verified bindings are displayed.")]
        [DefaultValue(true)]
        public bool ShowUnverifiedBindings { get; set; } = true;

        [Category("Rename")]
        [DisplayName("Rename unverified bindings")]
        [Description("Include XAML bindings whose DataContext could not be verified. When false, only verified bindings are renamed.")]
        [DefaultValue(false)]
        public bool RenameUnverifiedBindings { get; set; } = false;

        // --- Mouse+Keyboard shortcuts ---

        [Category("Navigation Shortcuts")]
        [DisplayName("Ctrl+Click action")]
        [Description("Action when Ctrl+Click in a XAML editor only. In C# editors, Ctrl+Click may be used by VS's built-in Go To Definition.")]
        [DefaultValue(CtrlClickAction.GoToDefinition)]
        [TypeConverter(typeof(EnumConverter))]
        public CtrlClickAction CtrlClickBehavior { get; set; } = CtrlClickAction.GoToDefinition;

        [Category("Navigation Shortcuts")]
        [DisplayName("Ctrl+Shift+Click action")]
        [Description("Action when Ctrl+Shift+Click. Works in both XAML and C# editors.")]
        [DefaultValue(CtrlClickAction.FindAllReferences)]
        [TypeConverter(typeof(EnumConverter))]
        public CtrlClickAction CtrlShiftClickBehavior { get; set; } = CtrlClickAction.FindAllReferences;

        [Category("Navigation Shortcuts")]
        [DisplayName("Ctrl+Alt+Click action")]
        [Description("Action when Ctrl+Alt+Click in a XAML editor only. In C# editors, Ctrl+Alt+Click may be used by VS's built-in Go To Definition.")]
        [DefaultValue(CtrlClickAction.None)]
        [TypeConverter(typeof(EnumConverter))]
        public CtrlClickAction CtrlAltClickBehavior { get; set; } = CtrlClickAction.None;
    }
}
