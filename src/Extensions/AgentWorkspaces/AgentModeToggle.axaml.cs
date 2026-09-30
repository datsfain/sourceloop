using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SourceGit.Extensions.AgentWorkspaces
{
    public partial class AgentModeToggle : UserControl
    {
        public AgentModeToggle()
        {
            InitializeComponent();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            DataContext = AgentMode.Instance;
            AgentWorkspacesExtension.AttachCommentHotkey(TopLevel.GetTopLevel(this));
        }

        private void OnToggle(object sender, RoutedEventArgs e)
        {
            AgentMode.Instance.Toggle();
            e.Handled = true;
        }
    }
}
