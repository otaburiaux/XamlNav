using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace XamlNav
{
    /// <summary>
    /// Information about a single binding path found in XAML content.
    /// </summary>
    public class BindingInfo
    {
        /// <summary>The binding path text, e.g. "Items.MyProperty". May be a dotted path.</summary>
        public string PropertyName { get; internal set; }

        /// <summary>Character offset of <see cref="PropertyName"/> within the XAML content string.</summary>
        public int StartPosition { get; internal set; }

        /// <summary>Length of <see cref="PropertyName"/> in characters.</summary>
        public int Length { get; internal set; }

        /// <summary>Character offset of the opening brace of the full {Binding ...} expression.</summary>
        public int BindingExpressionStart { get; internal set; }
    }

    /// <summary>
    /// Parses XAML content to find binding expressions.
    /// </summary>
    public static class XamlBindingParser
    {
        // Pattern for {Binding Path=Prop} or {Binding Prop} or {x:Bind Prop}
        // Matches: {Binding Path=MyProperty}, {Binding MyProperty}, {x:Bind MyProperty}
        private static readonly Regex BindingRegex = new Regex(@"\{(?:Binding|x:Bind)\s+(?:Path=)?(?<path>[a-zA-Z_][a-zA-Z0-9_.]*)", RegexOptions.Compiled);

        /// <summary>
        /// Parses XAML content and returns all found binding paths.
        /// </summary>
        public static IEnumerable<BindingInfo> ParseBindings(string xamlContent)
        {
            if (string.IsNullOrEmpty(xamlContent))
                yield break;

            var matches = BindingRegex.Matches(xamlContent);
            foreach (Match match in matches)
            {
                var pathGroup = match.Groups["path"];
                if (pathGroup.Success)
                {
                    // For paths like "Item.Name", we might want the root property "Item" 
                    // or the whole path depending on what we're searching for.
                    // For now, we return the whole path.
                    yield return new BindingInfo
                    {
                        PropertyName = pathGroup.Value,
                        StartPosition = pathGroup.Index,
                        Length = pathGroup.Length,
                        BindingExpressionStart = match.Index
                    };
                }
            }
        }

        /// <summary>
        /// Replaces a matching segment in a dotted binding path with a new name.
        /// E.g. ReplaceSegmentInPath("Items.OldProp.Sub", "OldProp", "NewProp") → "Items.NewProp.Sub"
        /// Returns the original path unchanged if no segment matches.
        /// </summary>
        public static string ReplaceSegmentInPath(string path, string oldName, string newName)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName))
                return path;

            var segments = path.Split('.');
            bool changed = false;
            for (int i = 0; i < segments.Length; i++)
            {
                if (segments[i] == oldName)
                {
                    segments[i] = newName;
                    changed = true;
                }
            }
            return changed ? string.Join(".", segments) : path;
        }
    }
}
