using System;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SourceGit.Extensions.AgentWorkspaces
{
    public partial class ReviewCommentDialog : Views.ChromelessWindow
    {
        public ReviewCommentDialog()
        {
            InitializeComponent();
            TxtComment.AddHandler(KeyDownEvent, OnCommentKeyDown, RoutingStrategies.Tunnel);
        }

        public ReviewCommentDialog(ReviewComment draft) : this()
        {
            TxtLocation.Text = draft.Label;
            TxtSnippet.Text = draft.Snippet;
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            TxtComment.Focus();
        }

        private void OnCommentKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                Submit();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Close(null);
                e.Handled = true;
            }
        }

        private void OnAdd(object sender, RoutedEventArgs e)
        {
            Submit();
            e.Handled = true;
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            Close(null);
            e.Handled = true;
        }

        private void Submit()
        {
            if (!string.IsNullOrWhiteSpace(TxtComment.Text))
                Close(TxtComment.Text);
        }
    }
}
