using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace SourceGit.Extensions.AgentWorkspaces
{
    public partial class DiffRevisionPicker : UserControl
    {
        public DiffRevisionPicker()
        {
            InitializeComponent();
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);
            Dispatcher.UIThread.Post(() => FilterTextBox.Focus(NavigationMethod.Directional));
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (DataContext is not DiffRevisionPickerModel vm)
                return;

            if (e.Key == Key.Enter)
            {
                vm.Confirm();
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                if (OptionsListBox.IsKeyboardFocusWithin)
                {
                    FilterTextBox.Focus(NavigationMethod.Directional);
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Down || e.Key == Key.Tab)
            {
                if (FilterTextBox.IsKeyboardFocusWithin)
                {
                    if (vm.Options.Count > 0)
                        OptionsListBox.Focus(NavigationMethod.Directional);

                    e.Handled = true;
                    return;
                }

                if (OptionsListBox.IsKeyboardFocusWithin && e.Key == Key.Tab)
                {
                    FilterTextBox.Focus(NavigationMethod.Directional);
                    e.Handled = true;
                }
            }
        }

        private void OnItemTapped(object sender, TappedEventArgs e)
        {
            if (DataContext is DiffRevisionPickerModel vm)
            {
                vm.Confirm();
                e.Handled = true;
            }
        }
    }
}
