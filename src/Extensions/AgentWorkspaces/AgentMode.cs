using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.Extensions.AgentWorkspaces
{
    public class AgentWorkspaceItem(ViewModels.Workspace workspace, string repoNames) : ObservableObject
    {
        public ViewModels.Workspace Workspace { get; } = workspace;
        public string RepoNames { get; } = repoNames;

        public bool IsMatch
        {
            get => _isMatch;
            private set => SetProperty(ref _isMatch, value);
        }

        public bool IsDimmed
        {
            get => _isDimmed;
            private set => SetProperty(ref _isDimmed, value);
        }

        public void ApplySearch(string query)
        {
            IsMatch = !string.IsNullOrEmpty(query) &&
                (Workspace.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || RepoNames.Contains(query, StringComparison.OrdinalIgnoreCase));
            IsDimmed = !string.IsNullOrEmpty(query) && !IsMatch;
        }

        private bool _isMatch;
        private bool _isDimmed;
    }

    public class AgentMode : ObservableObject
    {
        public static AgentMode Instance => s_instance ??= new AgentMode();

        public bool IsActive
        {
            get => _isActive;
            private set => SetProperty(ref _isActive, value);
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (SetProperty(ref _isExpanded, value))
                {
                    if (value)
                        File.Delete(CollapsedFlagFile);
                    else
                        File.WriteAllText(CollapsedFlagFile, string.Empty);

                    OnPropertyChanged(nameof(PanelWidth));
                    OnPropertyChanged(nameof(ShowEmptyHint));
                }
            }
        }

        public double PanelWidth => _isExpanded ? _width : CollapsedWidth;

        public List<AgentWorkspaceItem> Items
        {
            get => _items;
            private set
            {
                if (SetProperty(ref _items, value))
                {
                    OnPropertyChanged(nameof(HasItems));
                    OnPropertyChanged(nameof(ShowEmptyHint));
                }
            }
        }

        public bool HasItems => _items.Count > 0;

        public bool IsSearching
        {
            get => _isSearching;
            private set => SetProperty(ref _isSearching, value);
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value))
                    ApplySearch();
            }
        }

        public bool ShowEmptyHint => _isExpanded && _items.Count == 0;

        public AvaloniaList<ReviewComment> Comments
        {
            get => _comments;
            private set
            {
                if (SetProperty(ref _comments, value))
                    OnPropertyChanged(nameof(CanSubmitReview));
            }
        }

        public string ReviewSummary
        {
            get => _reviewSummary;
            set
            {
                if (SetProperty(ref _reviewSummary, value))
                    OnPropertyChanged(nameof(CanSubmitReview));
            }
        }

        public bool CanSubmitReview => _comments.Count > 0 || !string.IsNullOrWhiteSpace(_reviewSummary);

        public string ReviewStatus
        {
            get => _reviewStatus;
            private set => SetProperty(ref _reviewStatus, value);
        }

        private AgentMode()
        {
            AgentRegistry.Start();
            AgentRegistry.Changed += Refresh;

            _isExpanded = !File.Exists(CollapsedFlagFile);
            if (File.Exists(WidthFile) && double.TryParse(File.ReadAllText(WidthFile), CultureInfo.InvariantCulture, out var width))
                _width = Math.Clamp(width, MinWidth, MaxWidth);
            _launcher = App.GetLauncher();
            _launcher.PropertyChanged += OnLauncherPropertyChanged;
            Refresh();
        }

        public void Toggle()
        {
            if (_isActive)
                Activate(_lastNormal ?? ViewModels.Preferences.Instance.GetActiveWorkspace());
            else
                Activate(AgentRegistry.IsAgentWorkspace(_lastAgent) ? _lastAgent : AgentRegistry.Workspaces.FirstOrDefault() ?? _empty);
        }

        public void Activate(ViewModels.Workspace to)
        {
            if (to == null || to == _launcher.ActiveWorkspace)
                return;

            var normal = _isActive ? _lastNormal : _launcher.ActiveWorkspace;
            foreach (var w in AgentRegistry.Workspaces)
                w.IsActive = false;
            _empty.IsActive = false;
            if (normal != null)
                normal.IsActive = false;

            _launcher.SwitchWorkspace(to);

            if (!IsAgent(to))
                return;

            // Keep the normal workspace marked active so it is the one restored on next startup.
            _lastAgent = to;
            _lastNormal = normal;
            if (normal != null)
            {
                normal.IsActive = true;
                ViewModels.Preferences.Instance.Save();
            }

            foreach (var page in _launcher.Pages)
            {
                if (page.Data is ViewModels.Repository repo)
                    ShowFirstLocalChange(repo);
            }
        }

        public void StartSearch()
        {
            IsSearching = true;
        }

        public void StopSearch()
        {
            SearchText = string.Empty;
            IsSearching = false;
        }

        public void ActivateFirstMatch()
        {
            if (_items.FirstOrDefault(i => i.IsMatch) is { } match)
                Activate(match.Workspace);
        }

        public void AddComment(ReviewComment comment)
        {
            _comments.Add(comment);
            ReviewStatus = null;
            OnPropertyChanged(nameof(CanSubmitReview));
        }

        public void RemoveComment(ReviewComment comment)
        {
            _comments.Remove(comment);
            OnPropertyChanged(nameof(CanSubmitReview));
        }

        // Dragging below the threshold collapses the panel; dragging back out expands it again.
        public void ResizeTo(double width)
        {
            IsExpanded = width >= CollapseBelow;
            if (!_isExpanded)
                return;

            _width = Math.Clamp(width, MinWidth, MaxWidth);
            OnPropertyChanged(nameof(PanelWidth));
        }

        public void SaveWidth()
        {
            File.WriteAllText(WidthFile, _width.ToString(CultureInfo.InvariantCulture));
        }

        public async Task SubmitReviewAsync()
        {
            var workspace = _launcher.ActiveWorkspace;
            var socket = AgentRegistry.GetSocket(workspace);
            if (!CanSubmitReview)
                return;

            if (string.IsNullOrEmpty(socket))
            {
                ReviewStatus = "No Claude session registered for this workspace (run sgws show from the session).";
                return;
            }

            try
            {
                await Review.SendAsync(socket, Review.Format([.. _comments], _reviewSummary));
                ReviewStatus = _comments.Count > 0 ? $"Sent {_comments.Count} comment(s) to the agent." : "Sent the note to the agent.";
                _comments.Clear();
                ReviewSummary = string.Empty;
                OnPropertyChanged(nameof(CanSubmitReview));
            }
            catch (Exception e)
            {
                ReviewStatus = $"Could not reach the Claude session (closed?): {e.Message}";
            }
        }

        public void Refresh()
        {
            var active = _launcher.ActiveWorkspace;
            Items = AgentRegistry.Workspaces
                .Select(w => new AgentWorkspaceItem(w, string.Join(" · ", w.Repositories.Select(Path.GetFileName))))
                .ToList();
            ApplySearch();

            var isRemovedAgentWorkspace = active != null && !IsAgent(active) && !ViewModels.Preferences.Instance.Workspaces.Contains(active);
            if (isRemovedAgentWorkspace)
                Activate(AgentRegistry.Workspaces.FirstOrDefault() ?? _empty);
            else
                IsActive = IsAgent(active);

            var review = AgentRegistry.IsAgentWorkspace(_launcher.ActiveWorkspace) ? _launcher.ActiveWorkspace : null;
            if (review != _reviewWorkspace)
            {
                if (_reviewWorkspace != null)
                    _drafts[_reviewWorkspace] = (_comments, _reviewSummary);

                _reviewWorkspace = review;
                var (comments, summary) = review != null && _drafts.TryGetValue(review, out var draft) ? draft : ([], string.Empty);
                Comments = comments;
                ReviewSummary = summary;
                ReviewStatus = null;
            }

            if (AgentRegistry.TakeFocusRequest() is { } focus)
            {
                Activate(focus);
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: Views.Launcher window })
                    window.BringToTop();
            }
        }

        private void ApplySearch()
        {
            var query = _searchText?.Trim();
            foreach (var item in _items)
                item.ApplySearch(query);
        }

        private void OnLauncherPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(ViewModels.Launcher.ActiveWorkspace) or nameof(ViewModels.Launcher.ActivePage))
                Refresh();
        }

        private bool IsAgent(ViewModels.Workspace workspace)
        {
            return workspace == _empty || AgentRegistry.IsAgentWorkspace(workspace);
        }

        private static void ShowFirstLocalChange(ViewModels.Repository repo)
        {
            repo.SelectedViewIndex = 1;

            var workingCopy = repo.WorkingCopy;
            if (SelectFirstChange(workingCopy))
                return;

            PropertyChangedEventHandler handler = null;
            handler = (_, e) =>
            {
                if (e.PropertyName is not (nameof(ViewModels.WorkingCopy.Unstaged) or nameof(ViewModels.WorkingCopy.Staged)))
                    return;

                workingCopy.PropertyChanged -= handler;
                Dispatcher.UIThread.Post(() => SelectFirstChange(workingCopy));
            };
            workingCopy.PropertyChanged += handler;
        }

        private static bool SelectFirstChange(ViewModels.WorkingCopy workingCopy)
        {
            if (workingCopy.VisibleUnstaged is { Count: > 0 } unstaged)
                workingCopy.SelectedUnstaged = new(new List<Models.Change> { FirstByPath(unstaged) });
            else if (workingCopy.VisibleStaged is { Count: > 0 } staged)
                workingCopy.SelectedStaged = new(new List<Models.Change> { FirstByPath(staged) });
            else
                return false;

            return true;
        }

        private static Models.Change FirstByPath(List<Models.Change> changes)
        {
            return changes.Aggregate((l, r) => Models.NumericSort.Compare(l.Path, r.Path) <= 0 ? l : r);
        }

        private static string CollapsedFlagFile => Path.Combine(AgentRegistry.Dir, ".sidebar-collapsed");
        private static string WidthFile => Path.Combine(AgentRegistry.Dir, ".sidebar-width");

        private const double CollapsedWidth = 33;
        private const double CollapseBelow = 56;
        private const double MinWidth = 72;
        private const double MaxWidth = 640;

        private static AgentMode s_instance;

        private readonly ViewModels.Launcher _launcher;
        private bool _isActive;
        private bool _isExpanded;
        private double _width = 257;
        private List<AgentWorkspaceItem> _items = [];
        private bool _isSearching;
        private string _searchText = string.Empty;
        private ViewModels.Workspace _lastNormal;
        private ViewModels.Workspace _lastAgent;
        private ViewModels.Workspace _reviewWorkspace;
        private readonly ViewModels.Workspace _empty = new() { Name = "Agents" };
        private AvaloniaList<ReviewComment> _comments = [];
        private string _reviewSummary = string.Empty;
        private string _reviewStatus;
        private readonly Dictionary<ViewModels.Workspace, (AvaloniaList<ReviewComment>, string)> _drafts = [];
    }
}
