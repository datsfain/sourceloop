using System;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SourceGit.Views;

namespace SourceGit.Extensions.AgentWorkspaces
{
    public partial class DiffPageView : UserControl
    {
        public DiffPageView()
        {
            InitializeComponent();

            // Handled while tunnelling so the diff editors cannot swallow the file navigation keys.
            AddHandler(KeyDownEvent, OnNavigationKeyDown, RoutingStrategies.Tunnel);
            DataContextChanged += (_, _) => Hook();
        }

        private void Hook()
        {
            if (_hooked is { } old)
                old.PropertyChanged -= OnPageChanged;

            _hooked = DataContext as DiffPage;
            if (_hooked != null)
            {
                _hooked.PropertyChanged += OnPageChanged;
                ApplyCollapsed();
            }
        }

        private void OnPageChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DiffPage.IsFileListCollapsed))
                ApplyCollapsed();
        }

        // Collapsing clamps the column instead of changing its Width, so the width saved in the layout preferences stays intact.
        private void ApplyCollapsed()
        {
            if (_hooked == null)
                return;

            var files = Body.ColumnDefinitions[0];
            var splitter = Body.ColumnDefinitions[1];
            var collapsed = _hooked.IsFileListCollapsed;
            files.MinWidth = collapsed ? 0 : 200;
            files.MaxWidth = collapsed ? 0 : double.PositiveInfinity;
            splitter.Width = new GridLength(collapsed ? 0 : 4);
            Splitter.IsVisible = !collapsed;
        }

        private void OnNavigationKeyDown(object sender, KeyEventArgs e)
        {
            if (DataContext is not DiffPage page)
                return;

            // V toggles "viewed" wherever focus is in this page (file list or diff), except while typing in a text box such as the filter.
            if (e.Key == Key.V && e.KeyModifiers == KeyModifiers.None)
            {
                if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not TextBox)
                {
                    page.ToggleViewed();
                    e.Handled = true;
                }

                return;
            }

            if (e.KeyModifiers != KeyModifiers.Alt)
                return;

            if (e.Key == Key.Down)
            {
                page.SelectNext();
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                page.SelectPrevious();
                e.Handled = true;
            }
        }

        private async void OnFilesKeyDown(object sender, KeyEventArgs e)
        {
            if (DataContext is not DiffPage { ChangeSelection: { Count: > 0 } selection } page)
                return;

            var cmdKey = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            if (e.Key == Key.C && e.KeyModifiers.HasFlag(cmdKey))
            {
                var builder = new StringBuilder();
                var absolute = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                foreach (var c in selection.Changes)
                    builder.AppendLine(absolute ? page.GetAbsPath(c.Path) : c.Path);

                await this.CopyTextAsync(builder.ToString().TrimEnd());
                e.Handled = true;
            }
            else if (e.Key == Key.F && e.KeyModifiers == cmdKey)
            {
                SearchBox.Focus();
                e.Handled = true;
            }
        }

        private void OnPickBase(object sender, TappedEventArgs e)
        {
            ShowPicker(BaseButton, revision => _ = (DataContext as DiffPage)?.SetBaseAsync(revision));
            e.Handled = true;
        }

        private void OnPickTarget(object sender, TappedEventArgs e)
        {
            ShowPicker(TargetButton, revision => _ = (DataContext as DiffPage)?.SetTargetAsync(revision));
            e.Handled = true;
        }

        // HEAD, the worktree, branches and tags; anything else typed in the filter is tried as a revision.
        private void ShowPicker(Control anchor, Action<DiffRevision> picked)
        {
            if (DataContext is not DiffPage page)
                return;

            var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
            flyout.Content = new DiffRevisionPicker
            {
                DataContext = new DiffRevisionPickerModel(page, revision =>
                {
                    flyout.Hide();
                    picked(revision);
                }),
            };
            flyout.ShowAt(anchor);
        }

        private DiffPage _hooked;
    }
}
