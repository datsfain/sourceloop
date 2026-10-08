using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

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
            if (sender is Control { DataContext: AgentWorkspaceItem item })
                AgentMode.Instance.Activate(item.Workspace);

            e.Handled = true;
        }

        private bool _isResizing;
    }
}
