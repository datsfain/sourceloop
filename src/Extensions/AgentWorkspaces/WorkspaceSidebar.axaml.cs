using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace SourceGit.Extensions.AgentWorkspaces
{
    public partial class WorkspaceSidebar : UserControl
    {
        public WorkspaceSidebar()
        {
            InitializeComponent();
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

        private async void OnSubmitReview(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            await AgentMode.Instance.SubmitReviewAsync();
        }

        private void OnListTapped(object sender, TappedEventArgs e)
        {
            if (!AgentMode.Instance.IsExpanded)
                return;

            AgentMode.Instance.StartSearch();
            Dispatcher.UIThread.Post(() => SearchBox.Focus());
            e.Handled = true;
        }

        private void OnSearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                AgentMode.Instance.StopSearch();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                AgentMode.Instance.ActivateFirstMatch();
                AgentMode.Instance.StopSearch();
                e.Handled = true;
            }
        }

        private void OnSearchLostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(AgentMode.Instance.SearchText))
                AgentMode.Instance.StopSearch();
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
