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
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            DataContext = AgentMode.Instance;
        }

        private void OnToggleExpanded(object sender, RoutedEventArgs e)
        {
            AgentMode.Instance.IsExpanded = !AgentMode.Instance.IsExpanded;
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

        private void OnItemTapped(object sender, TappedEventArgs e)
        {
            if (sender is Control { DataContext: AgentWorkspaceItem item })
                AgentMode.Instance.Activate(item.Workspace);

            e.Handled = true;
        }
    }
}
