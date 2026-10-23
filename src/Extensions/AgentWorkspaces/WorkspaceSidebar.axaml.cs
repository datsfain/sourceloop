using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SourceGit.Extensions.AgentWorkspaces
{
    public partial class WorkspaceSidebar : UserControl
    {
        public WorkspaceSidebar()
        {
            InitializeComponent();

            // The TextBox consumes Enter itself, so listen while tunnelling. Shift+Enter keeps inserting a new line.
            NoteBox.AddHandler(KeyDownEvent, OnNoteKeyDown, RoutingStrategies.Tunnel);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            DataContext = AgentMode.Instance;
        }

        private void OnResizePressed(object sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;

            _isResizing = true;
            e.Pointer.Capture(sender as IInputElement);
            e.Handled = true;
        }

        private void OnResizeMoved(object sender, PointerEventArgs e)
        {
            if (!_isResizing)
                return;

            AgentMode.Instance.ResizeTo(e.GetPosition(this).X);
            e.Handled = true;
        }

        private void OnResizeReleased(object sender, PointerReleasedEventArgs e)
        {
            if (!_isResizing)
                return;

            _isResizing = false;
            e.Pointer.Capture(null);
            AgentMode.Instance.SaveWidth();
            e.Handled = true;
        }

        private void OnRemoveComment(object sender, RoutedEventArgs e)
        {
            if (sender is Control { DataContext: ReviewComment comment })
                AgentMode.Instance.RemoveComment(comment);

            e.Handled = true;
        }

        private async void OnNoteKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                return;

            e.Handled = true;
            await AgentMode.Instance.SubmitReviewAsync();
        }

        private async void OnSubmitReview(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            await AgentMode.Instance.SubmitReviewAsync();
        }

        private void OnSearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                AgentMode.Instance.SearchText = string.Empty;
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                AgentMode.Instance.ActivateFirstMatch();
                e.Handled = true;
            }
        }

        private void OnRemoveWorkspace(object sender, RoutedEventArgs e)
        {
            if (sender is Control { DataContext: AgentWorkspaceItem item })
                AgentMode.Instance.RemoveWorkspace(item.Workspace);

            e.Handled = true;
        }

        private void OnSwallowTapped(object sender, TappedEventArgs e)
        {
            e.Handled = true;
        }

        private void OnItemTapped(object sender, TappedEventArgs e)
        {
            if (sender is Control { DataContext: AgentWorkspaceItem { IsEditing: false } item } && !_justDragged)
                AgentMode.Instance.Activate(item.Workspace);

            e.Handled = true;
        }

        private void OnSwallowDoubleTapped(object sender, TappedEventArgs e)
        {
            e.Handled = true;
        }

        private void OnGoBack(object sender, RoutedEventArgs e)
        {
            AgentMode.Instance.GoBack();
            e.Handled = true;
        }

        private void OnGoForward(object sender, RoutedEventArgs e)
        {
            AgentMode.Instance.GoForward();
            e.Handled = true;
        }

        private async void OnEditComment(object sender, TappedEventArgs e)
        {
            e.Handled = true;
            if (sender is Control { DataContext: ReviewComment comment } control)
                await AgentWorkspacesExtension.EditCommentAsync(control, comment);
        }

        private void OnItemDoubleTapped(object sender, TappedEventArgs e)
        {
            if (sender is Grid grid)
                BeginRename(grid);

            e.Handled = true;
        }

        private void OnRenameWorkspace(object sender, RoutedEventArgs e)
        {
            if (PlacementTargetOf(sender) is Grid grid)
                BeginRename(grid);

            e.Handled = true;
        }

        private void OnMoveUp(object sender, RoutedEventArgs e) => MoveBy(sender, -1);

        private void OnMoveDown(object sender, RoutedEventArgs e) => MoveBy(sender, 1);

        private void MoveBy(object sender, int delta)
        {
            if (PlacementTargetOf(sender)?.DataContext is AgentWorkspaceItem item)
            {
                var items = AgentMode.Instance.Items;
                var index = items.IndexOf(item) + delta;
                if (index >= 0 && index < items.Count)
                    AgentMode.Instance.MoveTo(item.Workspace, index);
            }
        }

        private static Control PlacementTargetOf(object sender)
        {
            return ((sender as MenuItem)?.Parent as ContextMenu)?.PlacementTarget;
        }

        private static void BeginRename(Grid grid)
        {
            if (grid.DataContext is not AgentWorkspaceItem item)
                return;

            var box = grid.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "RenameBox");
            if (box == null)
                return;

            box.Text = item.Workspace.Name;
            item.IsEditing = true;
            Dispatcher.UIThread.Post(() =>
            {
                box.Focus();
                box.SelectAll();
            });
        }

        private void OnRenameKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox { DataContext: AgentWorkspaceItem item } box || (e.Key != Key.Enter && e.Key != Key.Escape))
                return;

            e.Handled = true;
            if (e.Key == Key.Enter)
                AgentMode.Instance.Rename(item.Workspace, box.Text);
            item.IsEditing = false;
        }

        private void OnRenameLostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox { DataContext: AgentWorkspaceItem { IsEditing: true } item } box)
            {
                AgentMode.Instance.Rename(item.Workspace, box.Text);
                item.IsEditing = false;
            }
        }

        // Dragging an item over another one drops it into that slot.
        private void OnItemPointerPressed(object sender, PointerPressedEventArgs e)
        {
            _justDragged = false;
            if (sender is Control { DataContext: AgentWorkspaceItem { IsEditing: false } item } && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _dragItem = item;
                _dragStart = e.GetPosition(this);
            }
        }

        private void OnItemPointerMoved(object sender, PointerEventArgs e)
        {
            if (_dragItem == null)
                return;

            if (!_isDragging)
            {
                if (System.Math.Abs(e.GetPosition(this).Y - _dragStart.Y) < 6)
                    return;

                _isDragging = true;
                _dragItem.IsDragged = true;
                e.Pointer.Capture(sender as IInputElement);
            }

            ShowDropLine(ItemAt(e));
        }

        // The line sits where the dragged item will land: above a target higher up the list, below one further down.
        private void ShowDropLine(AgentWorkspaceItem target)
        {
            var items = AgentMode.Instance.Items;
            var from = items.IndexOf(_dragItem);
            var to = target == null ? -1 : items.IndexOf(target);
            foreach (var item in items)
            {
                var index = items.IndexOf(item);
                item.IsDropAbove = index == to && to < from;
                item.IsDropBelow = index == to && to > from;
            }
        }

        private void OnItemPointerReleased(object sender, PointerReleasedEventArgs e)
        {
            var item = _dragItem;
            var wasDragging = _isDragging;
            _dragItem = null;
            _isDragging = false;
            if (item == null || !wasDragging)
                return;

            e.Pointer.Capture(null);
            _justDragged = true;
            e.Handled = true;

            var target = ItemAt(e);
            item.IsDragged = false;
            ShowDropLine(null);

            if (target != null && target != item)
                AgentMode.Instance.MoveTo(item.Workspace, AgentMode.Instance.Items.IndexOf(target));
        }

        private AgentWorkspaceItem ItemAt(PointerEventArgs e)
        {
            var panel = WorkspaceList.ItemsPanelRoot;
            var items = AgentMode.Instance.Items;
            if (panel == null || items.Count == 0)
                return null;

            var y = e.GetPosition(panel).Y;
            var index = panel.Children.Count - 1;
            for (var i = 0; i < panel.Children.Count; i++)
            {
                if (y < panel.Children[i].Bounds.Bottom)
                {
                    index = i;
                    break;
                }
            }

            return index < items.Count ? items[index] : null;
        }

        private AgentWorkspaceItem _dragItem;
        private Point _dragStart;
        private bool _isDragging;
        private bool _justDragged;

        private bool _isResizing;
    }
}
