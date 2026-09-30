using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaloniaEdit;
using SourceGit.Views;

namespace SourceGit.Extensions.AgentWorkspaces
{
    internal static class AgentWorkspacesExtension
    {
        [ModuleInitializer]
        internal static void Register()
        {
            ExtensionHost.Register(ExtensionPoint.LauncherTitleBar, () => new AgentModeToggle());
            ExtensionHost.Register(ExtensionPoint.LauncherSidePanel, () => new WorkspaceSidebar());
            ExtensionHost.Register(ExtensionPoint.DiffChunkPopup, CreateChunkCommentButton);
            ExtensionHost.RegisterDiffContextMenu(AddReviewCommentItem);
        }

        public static void AttachCommentHotkey(TopLevel topLevel)
        {
            if (topLevel == null || s_attached.Contains(topLevel))
                return;

            s_attached.Add(topLevel);
            topLevel.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        }

        private static async void OnKeyDown(object sender, KeyEventArgs e)
        {
            // Plain C only, so Ctrl+C / Cmd+C keep copying.
            if (e.Key != Key.C || e.KeyModifiers != KeyModifiers.None || !AgentMode.Instance.IsActive || sender is not TopLevel topLevel)
                return;

            var focused = topLevel.FocusManager?.GetFocusedElement() as Visual;
            var diffEditor = focused?.FindAncestorOfType<ThemedTextDiffPresenter>(true);
            if (diffEditor == null && (focused is TextBox || focused?.FindAncestorOfType<TextEditor>(true) != null))
                return;

            if (diffEditor != null && !diffEditor.TextArea.Selection.IsEmpty)
            {
                e.Handled = true;
                await CommentOnSelectionAsync(diffEditor, GetLines(diffEditor));
                return;
            }

            var hovered = s_chunkButtons.FirstOrDefault(b => b.DataContext is ViewModels.TextDiffContext { SelectedChunk: not null });
            if (hovered != null)
            {
                e.Handled = true;
                await CommentOnChunkAsync(hovered);
                return;
            }

            if (focused?.FindAncestorOfType<ChangeCollectionView>(true) != null &&
                focused.FindAncestorOfType<Views.WorkingCopy>()?.DataContext is ViewModels.WorkingCopy workingCopy)
            {
                e.Handled = true;
                await CommentOnFilesAsync((Control)focused, workingCopy);
            }
        }

        private static Control CreateChunkCommentButton()
        {
            var hint = new Run("C") { FontSize = 11, FontWeight = FontWeight.Normal };
            var label = new TextBlock();
            label.Inlines!.Add(new Run("Comment "));
            label.Inlines.Add(hint);

            var button = new Button { Content = label, Margin = new Thickness(8, 0, 0, 0) };
            ToolTip.SetTip(button, "Comment on this change for the agent (C)");
            button.Classes.Add("flat");
            button.Bind(Button.BorderBrushProperty, button.GetResourceObservable("Brush.FlatButton.FloatingBorder"));
            hint.Bind(TextElement.ForegroundProperty, button.GetResourceObservable("MenuFlyoutItemKeyboardAcceleratorTextForeground"));

            button.AttachedToVisualTree += (_, _) =>
            {
                button.IsVisible = AgentMode.Instance.IsActive;
                s_chunkButtons.Add(button);
            };
            button.DetachedFromVisualTree += (_, _) => s_chunkButtons.Remove(button);
            button.Click += async (_, e) =>
            {
                e.Handled = true;
                await CommentOnChunkAsync(button);
            };
            return button;
        }

        private static void AddReviewCommentItem(DiffContextMenuRequest request)
        {
            if (!AgentMode.Instance.IsActive)
                return;

            var item = new MenuItem { Header = "Add review comment…", Icon = request.Editor.CreateMenuIcon("Icons.Message"), InputGesture = new KeyGesture(Key.C) };
            item.Click += async (_, e) =>
            {
                e.Handled = true;
                await CommentOnSelectionAsync(request.Editor, request.Lines);
            };

            request.Menu.Items.Add(new MenuItem { Header = "-" });
            request.Menu.Items.Add(item);
        }

        private static async Task CommentOnChunkAsync(Control anchor)
        {
            if (anchor.DataContext is not ViewModels.TextDiffContext { SelectedChunk: { } chunk, Data: { } diff })
                return;

            var end = Math.Min(chunk.EndIdx, diff.Lines.Count - 1);
            if (chunk.StartIdx >= 0 && chunk.StartIdx <= end)
                await AddCommentAsync(anchor, diff.Lines.GetRange(chunk.StartIdx, end - chunk.StartIdx + 1));
        }

        private static async Task CommentOnSelectionAsync(TextEditor editor, List<Models.TextDiffLine> lines)
        {
            var selection = editor.TextArea.Selection;
            var start = Math.Min(selection.StartPosition.Line, selection.EndPosition.Line);
            var end = Math.Min(Math.Max(selection.StartPosition.Line, selection.EndPosition.Line), lines.Count);
            if (start >= 1 && start <= end)
                await AddCommentAsync(editor, lines.GetRange(start - 1, end - start + 1));
        }

        private static async Task CommentOnFilesAsync(Control anchor, ViewModels.WorkingCopy workingCopy)
        {
            var isUnstaged = workingCopy.SelectedUnstaged is { Count: > 0 };
            var selected = isUnstaged ? workingCopy.SelectedUnstaged : workingCopy.SelectedStaged;
            if (selected is not { Count: > 0 } ||
                anchor.FindAncestorOfType<Views.Repository>()?.DataContext is not ViewModels.Repository repo)
                return;

            await ShowDialogAsync(anchor, Review.CreateFileDraft(repo.FullPath, selected.Changes, isUnstaged));
        }

        private static async Task AddCommentAsync(Control anchor, List<Models.TextDiffLine> lines)
        {
            if (anchor.DataContext is not ViewModels.TextDiffContext { Option: { } option } ||
                anchor.FindAncestorOfType<Views.Repository>()?.DataContext is not ViewModels.Repository repo)
                return;

            await ShowDialogAsync(anchor, Review.CreateDraft(repo.FullPath, option, lines));
        }

        private static async Task ShowDialogAsync(Control anchor, ReviewComment draft)
        {
            if (TopLevel.GetTopLevel(anchor) is not Window owner)
                return;

            var text = await new ReviewCommentDialog(draft).ShowDialog<string>(owner);
            if (!string.IsNullOrWhiteSpace(text))
                AgentMode.Instance.AddComment(draft with { Text = text });
        }

        private static List<Models.TextDiffLine> GetLines(ThemedTextDiffPresenter editor)
        {
            return editor.DataContext switch
            {
                ViewModels.CombinedTextDiff combined => combined.Data.Lines,
                ViewModels.TwoSideTextDiff twoSides => editor.IsOld ? twoSides.Old : twoSides.New,
                _ => [],
            };
        }

        private static readonly List<Control> s_chunkButtons = [];
        private static readonly HashSet<TopLevel> s_attached = [];
    }
}
