using System;
using System.Collections.Generic;
using Avalonia.Controls;
using AvaloniaEdit;

namespace SourceGit.Extensions
{
    public enum ExtensionPoint
    {
        LauncherTitleBar,
        LauncherSidePanel,
        DiffChunkPopup,
    }

    public record DiffContextMenuRequest(TextEditor Editor, List<Models.TextDiffLine> Lines, bool IsOldSide, ContextMenu Menu);

    // Compile-time extensions register from a [ModuleInitializer], which keeps them NativeAOT-compatible.
    public static class ExtensionHost
    {
        public static void Register(ExtensionPoint point, Func<Control> factory)
        {
            if (!s_factories.TryGetValue(point, out var factories))
                s_factories[point] = factories = [];

            factories.Add(factory);
        }

        public static IReadOnlyList<Func<Control>> GetFactories(ExtensionPoint point)
        {
            return s_factories.TryGetValue(point, out var factories) ? factories : [];
        }

        public static void RegisterDiffContextMenu(Action<DiffContextMenuRequest> contributor)
        {
            s_diffContextMenuContributors.Add(contributor);
        }

        public static void ExtendDiffContextMenu(TextEditor editor, List<Models.TextDiffLine> lines, bool isOldSide, ContextMenu menu)
        {
            var request = new DiffContextMenuRequest(editor, lines, isOldSide, menu);
            foreach (var contributor in s_diffContextMenuContributors)
                contributor(request);
        }

        private static readonly Dictionary<ExtensionPoint, List<Func<Control>>> s_factories = [];
        private static readonly List<Action<DiffContextMenuRequest>> s_diffContextMenuContributors = [];
    }
}
