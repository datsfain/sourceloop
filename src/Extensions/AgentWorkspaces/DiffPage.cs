using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.Extensions.AgentWorkspaces
{
    public enum DiffRevisionKind
    {
        Commit,
        Head,
        Worktree,
        Branch,
        Tag,
    }

    // One side of the Diff tab. An empty Sha means the working tree; a null Spec means the commit is fixed (not re-resolved on reload).
    public record DiffRevision(string Name, string Sha, string Spec, Models.Commit Commit = null, DiffRevisionKind Kind = DiffRevisionKind.Commit)
    {
        public static readonly DiffRevision Worktree = new("worktree", string.Empty, null, null, DiffRevisionKind.Worktree);

        public bool IsWorktree => Kind == DiffRevisionKind.Worktree;
        public bool HasCommit => Commit != null;
        public string ShortSha => IsWorktree ? string.Empty : Sha[..Math.Min(10, Sha.Length)];

        public object Icon => Avalonia.Application.Current?.FindResource(Kind switch
        {
            DiffRevisionKind.Head => "Icons.Head",
            DiffRevisionKind.Worktree => "Icons.Worktree",
            DiffRevisionKind.Branch => "Icons.Branch",
            DiffRevisionKind.Tag => "Icons.Tag",
            _ => "Icons.Commit",
        });
    }

    // The fourth repository page: pick two revisions and review every changed file with the whole window.
    public class DiffPage : ObservableObject
    {
        public DiffRevision Base
        {
            get => _base;
            private set => SetProperty(ref _base, value);
        }

        public DiffRevision Target
        {
            get => _target;
            private set => SetProperty(ref _target, value);
        }

        public bool UseMergeBase
        {
            get => _useMergeBase;
            set
            {
                if (SetProperty(ref _useMergeBase, value))
                    _ = LoadAsync();
            }
        }

        public bool IsLoading
        {
            get => _isLoading;
            private set => SetProperty(ref _isLoading, value);
        }

        public string Message
        {
            get => _message;
            private set => SetProperty(ref _message, value);
        }

        public bool IsFileListCollapsed
        {
            get => _isFileListCollapsed;
            set => SetProperty(ref _isFileListCollapsed, value);
        }

        public int TotalChanges
        {
            get => _totalChanges;
            private set => SetProperty(ref _totalChanges, value);
        }

        public int ViewedCount
        {
            get => _viewedCount;
            private set => SetProperty(ref _viewedCount, value);
        }

        public List<Models.Change> VisibleChanges
        {
            get => _visibleChanges;
            private set => SetProperty(ref _visibleChanges, value);
        }

        public ViewModels.ChangeSelection ChangeSelection
        {
            get => _changeSelection;
            set
            {
                if (SetProperty(ref _changeSelection, value))
                {
                    if (value is { Count: 1, HasFolder: false })
                        DiffContext = new ViewModels.DiffContext(_repo.FullPath, new Models.DiffOption(_effectiveBase, _effectiveTarget, value.Changes[0]), _diffContext);
                    else
                        DiffContext = null;

                    UpdateCurrentViewed();
                }
            }
        }

        // The open file is marked viewed, so its diff is shown blurred (visible even with the file list collapsed).
        public bool IsCurrentViewed
        {
            get => _isCurrentViewed;
            private set => SetProperty(ref _isCurrentViewed, value);
        }

        public string SearchFilter
        {
            get => _searchFilter;
            set
            {
                if (SetProperty(ref _searchFilter, value))
                    RefreshVisible(false);
            }
        }

        public ViewModels.DiffContext DiffContext
        {
            get => _diffContext;
            private set
            {
                var old = _diffContext;
                if (SetProperty(ref _diffContext, value))
                {
                    if (old != null)
                        old.PropertyChanged -= OnDiffContextChanged;
                    if (value != null)
                        value.PropertyChanged += OnDiffContextChanged;
                }
            }
        }

        // How a comment on this diff names its revisions, e.g. "HEAD..develop (a1b2c3d..e4f5a6b)".
        public string CommentTag => _commentTag;

        public DiffPage(ViewModels.Repository repo)
        {
            _repo = repo;
            AgentMode.Instance.PropertyChanged += OnAgentModeChanged;
        }

        public void Dispose()
        {
            AgentMode.Instance.PropertyChanged -= OnAgentModeChanged;
        }

        // Called whenever the tab is opened: sets up the default range once, and re-resolves moved refs afterwards.
        public void OnShown()
        {
            if (!_initialized)
            {
                _initialized = true;
                _ = _restoreState != null ? RestoreAsync(_restoreState) : InitializeAsync();
            }
            else
            {
                _ = LoadAsync(true);
            }
        }

        // What to remember about this page, or null while it has not been set up yet (so saved state is kept as it is).
        public DiffPageState Capture()
        {
            if (!_initialized || _base == null || _target == null)
                return null;

            var state = new DiffPageState
            {
                Base = new RevisionState(_base.Name, _base.Spec, _base.Sha),
                Target = new RevisionState(_target.Name, _target.Spec, _target.Sha),
                MergeBase = _useMergeBase,
                File = _changeSelection is { Count: 1, HasFolder: false } selection ? selection.Changes[0].Path : null,
                Filter = _searchFilter,
                Collapsed = _isFileListCollapsed,
            };

            // Only marks keyed by blob hashes survive a restart; the newest ones win when there are very many.
            foreach (var (key, at) in _viewed.Where(p => p.Key.StartsWith("b:", StringComparison.Ordinal)).OrderByDescending(p => p.Value).Take(MaxSavedViewed))
                state.Viewed[key] = at;

            return state;
        }

        // The saved state is applied the first time the tab is shown. When an agent already opened a specific diff,
        // only the viewed marks are merged in, so they are not lost when the next save writes this page's state.
        public void Restore(DiffPageState state)
        {
            if (!_initialized)
            {
                _restoreState = state;
                return;
            }

            MergeViewed(state);
        }

        private void MergeViewed(DiffPageState state)
        {
            foreach (var (key, at) in state.Viewed)
                _viewed.TryAdd(key, at);

            if (_allChanges == null)
                return;

            foreach (var c in _allChanges)
                c.IsViewed = _viewed.ContainsKey(ViewedKey(c));

            ViewedCount = _allChanges.Count(c => c.IsViewed);
            UpdateCurrentViewed();
        }

        // True shortly after an agent asked for a specific diff, so startup defaults do not switch away from it.
        public bool WasJustRequested => DateTime.UtcNow - _requestedAt < TimeSpan.FromSeconds(15);

        // Entry point for "sgws show --diff": opens the given revisions, any ref name or sha.
        public async Task ShowAsync(string baseSpec, string targetSpec, bool mergeBase)
        {
            _initialized = true;
            _requestedAt = DateTime.UtcNow;

            // Saved marks still apply to the files of the diff that was asked for.
            if (_restoreState != null)
            {
                foreach (var (key, at) in _restoreState.Viewed)
                    _viewed.TryAdd(key, at);
                _restoreState = null;
            }

            var b = await ResolveAsync(baseSpec);
            var t = await ResolveAsync(targetSpec);
            if (b == null || t == null)
            {
                Message = $"Could not resolve '{(b == null ? baseSpec : targetSpec)}'.";
                return;
            }

            Base = b;
            Target = t;
            SetProperty(ref _useMergeBase, mergeBase, nameof(UseMergeBase));
            await LoadAsync();
        }

        public async Task SetBaseAsync(DiffRevision revision)
        {
            if (await Complete(revision) is { } resolved)
            {
                Base = resolved;
                await LoadAsync();
            }
        }

        public async Task SetTargetAsync(DiffRevision revision)
        {
            if (await Complete(revision) is { } resolved)
            {
                Target = resolved;
                await LoadAsync();
            }
        }

        public void Swap()
        {
            (Base, Target) = (_target, _base);
            _ = LoadAsync();
        }

        public void Reload()
        {
            _ = LoadAsync(true);
        }

        public void ClearSearchFilter()
        {
            SearchFilter = string.Empty;
        }

        // Candidates for a revision slot, filtered by what the user typed.
        public List<DiffRevision> GetOptions(string filter)
        {
            filter = filter?.Trim() ?? string.Empty;
            var options = new List<DiffRevision>();

            void Add(string name, DiffRevisionKind kind)
            {
                if (filter.Length == 0 || name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    options.Add(new DiffRevision(name, string.Empty, name, null, kind));
            }

            Add("HEAD", DiffRevisionKind.Head);
            if (!_repo.IsBare && (filter.Length == 0 || DiffRevision.Worktree.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                options.Add(DiffRevision.Worktree);

            var branches = _repo.Branches.OrderBy(b => b.IsLocal ? 0 : 1).ThenBy(b => b.FriendlyName, StringComparer.OrdinalIgnoreCase);
            foreach (var b in branches)
            {
                if (!b.IsDetachedHead)
                    Add(b.FriendlyName, DiffRevisionKind.Branch);
            }

            foreach (var t in _repo.Tags.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
                Add(t.Name, DiffRevisionKind.Tag);

            if (filter.Length > 0 && IsSafeSpec(filter) && options.All(o => o.Name != filter))
                options.Add(new DiffRevision(filter, string.Empty, filter));

            return options;
        }

        public void ToggleViewed()
        {
            if (_changeSelection is not { Count: > 0 } selection)
                return;

            var mark = selection.Changes.Any(c => !c.IsViewed);
            foreach (var c in selection.Changes)
            {
                c.IsViewed = mark;
                if (mark)
                    _viewed[ViewedKey(c)] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                else
                    _viewed.Remove(ViewedKey(c));
            }

            ViewedCount = _allChanges?.Count(c => c.IsViewed) ?? 0;
            UpdateCurrentViewed();
        }

        // A file with no changes (or only line-ending changes) still counts as viewed, but there is no diff to blur, so no overlay.
        private bool IsCurrentEmpty => _diffContext?.Content is Models.NoOrEOLChange;

        private void UpdateCurrentViewed()
        {
            IsCurrentViewed = _changeSelection is { Count: 1, HasFolder: false } selection && selection.Changes[0].IsViewed && !IsCurrentEmpty;
        }

        private void OnDiffContextChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModels.DiffContext.Content))
                UpdateCurrentViewed();
        }

        public void SelectNext() => Step(1);

        public void SelectPrevious() => Step(-1);

        public string GetAbsPath(string path)
        {
            return Native.OS.GetAbsPath(_repo.FullPath, path);
        }

        private void Step(int delta)
        {
            if (_visibleChanges is not { Count: > 0 } changes)
                return;

            var current = _changeSelection is { Count: > 0 } ? changes.IndexOf(_changeSelection.Changes[delta > 0 ? _changeSelection.Count - 1 : 0]) : -1;
            var next = current < 0 ? (delta > 0 ? 0 : changes.Count - 1) : Math.Clamp(current + delta, 0, changes.Count - 1);
            ChangeSelection = new ViewModels.ChangeSelection(new List<Models.Change> { changes[next] });
        }

        private async Task InitializeAsync()
        {
            IsLoading = true;
            Message = null;

            var head = await ResolveAsync("HEAD");
            if (head == null)
            {
                IsLoading = false;
                Message = "This repository has no commits yet.";
                return;
            }

            Target = head;
            Base = await FindDefaultBaseAsync(head);
            await LoadAsync();
        }

        private async Task RestoreAsync(DiffPageState state)
        {
            IsLoading = true;
            Message = null;

            foreach (var (key, at) in state.Viewed)
                _viewed[key] = at;

            _searchFilter = state.Filter ?? string.Empty;
            OnPropertyChanged(nameof(SearchFilter));
            IsFileListCollapsed = state.Collapsed;
            SetProperty(ref _useMergeBase, state.MergeBase, nameof(UseMergeBase));
            _restoreFile = state.File;

            var b = state.Base != null ? await ResolveSavedAsync(state.Base) : null;
            var t = state.Target != null ? await ResolveSavedAsync(state.Target) : null;
            if (b == null || t == null)
            {
                // A saved branch may be gone by now; fall back to the default range and say so.
                await InitializeAsync();
                Message = "A saved revision no longer exists; showing the default range.";
                return;
            }

            Base = b;
            Target = t;
            await LoadAsync();
        }

        private async Task<DiffRevision> ResolveSavedAsync(RevisionState saved)
        {
            if (saved.Name == DiffRevision.Worktree.Name && saved.Sha.Length == 0)
                return DiffRevision.Worktree;
            if (saved.Spec != null)
                return await ResolveAsync(saved.Spec);
            if (saved.Name == "root")
                return new DiffRevision(saved.Name, saved.Sha, null);

            // A fixed commit, e.g. where the branch left the default branch.
            var commit = await ResolveAsync(saved.Sha);
            return commit == null ? null : await WithCommitAsync(new DiffRevision(saved.Name, saved.Sha, null));
        }

        // The point where HEAD left the default branch, so a feature branch shows exactly its own changes.
        private async Task<DiffRevision> FindDefaultBaseAsync(DiffRevision head)
        {
            var defaultBranch = await FindDefaultBranchAsync();
            if (defaultBranch != null)
            {
                var mergeBase = await new Commands.MergeBase(_repo.FullPath, defaultBranch, "HEAD").GetResultAsync();
                if (!string.IsNullOrEmpty(mergeBase) && mergeBase != head.Sha)
                    return await WithCommitAsync(new DiffRevision($"merge-base {defaultBranch}", mergeBase, null));
            }

            // On the default branch itself (or without one): show the latest commit.
            var parent = await ResolveAsync("HEAD^");
            if (parent != null)
                return parent;

            return new DiffRevision("root", Models.EmptyTreeHash.Guess(head.Sha), null);
        }

        private async Task<DiffRevision> WithCommitAsync(DiffRevision revision)
        {
            var commit = await new Commands.QuerySingleCommit(_repo.FullPath, revision.Sha).GetResultAsync();
            return revision with { Commit = commit };
        }

        private async Task<string> FindDefaultBranchAsync()
        {
            var cmd = new ReadCommand(_repo.FullPath, "symbolic-ref --short refs/remotes/origin/HEAD");
            var fromRemote = (await cmd.ReadAsync()).Trim();
            if (fromRemote.Length > 0 && _repo.Branches.Any(b => b.FriendlyName == fromRemote))
                return fromRemote;

            foreach (var name in new[] { "main", "master", "develop", "origin/main", "origin/master", "origin/develop", "upstream/main", "upstream/master" })
            {
                if (_repo.Branches.Any(b => b.FriendlyName == name && !b.IsCurrent))
                    return name;
            }

            return null;
        }

        private async Task<DiffRevision> Complete(DiffRevision revision)
        {
            if (revision == null)
                return null;
            if (revision.IsWorktree)
                return revision;

            var resolved = await ResolveAsync(revision.Spec ?? revision.Name);
            if (resolved == null)
                Message = $"Could not resolve '{revision.Name}'.";
            return resolved;
        }

        private async Task<DiffRevision> ResolveAsync(string spec)
        {
            if (string.Equals(spec, DiffRevision.Worktree.Name, StringComparison.OrdinalIgnoreCase))
                return DiffRevision.Worktree;
            if (!IsSafeSpec(spec))
                return null;

            var commit = await new Commands.QuerySingleCommit(_repo.FullPath, spec).GetResultAsync();
            if (commit == null)
                return null;

            // A raw sha is shown shortened, anything else as the ref the user picked.
            var isSha = Regex.IsMatch(spec, "^[0-9a-fA-F]{7,64}$");
            var kind = spec == "HEAD" ? DiffRevisionKind.Head
                : _repo.Branches.Any(b => b.FriendlyName == spec) ? DiffRevisionKind.Branch
                : _repo.Tags.Any(t => t.Name == spec) ? DiffRevisionKind.Tag
                : DiffRevisionKind.Commit;
            return new DiffRevision(isSha ? commit.SHA[..10] : spec, commit.SHA, spec, commit, kind);
        }

        private static bool IsSafeSpec(string spec)
        {
            return !string.IsNullOrWhiteSpace(spec) && spec[0] != '-' && Regex.IsMatch(spec, @"^[\w./@^~{}:+,#\-]+$");
        }

        private async Task LoadAsync(bool reresolve = false)
        {
            if (_base == null || _target == null)
                return;

            var version = ++_loadVersion;
            IsLoading = true;
            Message = null;

            if (reresolve)
            {
                var b = _base.Spec != null ? await ResolveAsync(_base.Spec) : _base;
                var t = _target.Spec != null ? await ResolveAsync(_target.Spec) : _target;
                if (version != _loadVersion)
                    return;

                if (b != null && b != _base)
                    Base = b;
                if (t != null && t != _target)
                    Target = t;
            }

            var start = _base.Sha;
            var end = _target.Sha;
            var separator = "..";
            if (_useMergeBase && start.Length > 0 && end.Length > 0)
            {
                var mergeBase = await new Commands.MergeBase(_repo.FullPath, start, end).GetResultAsync();
                if (!string.IsNullOrEmpty(mergeBase))
                {
                    start = mergeBase;
                    separator = "...";
                }
            }

            var changes = start == end
                ? []
                : await new Commands.CompareRevisions(_repo.FullPath, start, end).ReadAsync();

            if (version != _loadVersion)
                return;

            // Blob hashes identify a file's diff independently of the commits around it, so marks survive new commits and rebases.
            _blobs = start != end && start.Length > 0 && end.Length > 0
                ? await new RawDiffCommand(_repo.FullPath, start, end).ReadAsync()
                : [];
            if (version != _loadVersion)
                return;

            _effectiveBase = start;
            _effectiveTarget = end;
            foreach (var c in changes)
                c.IsViewed = _viewed.ContainsKey(ViewedKey(c));

            var shas = $"{Short(start)}..{Short(end)}";
            var tag = IsPlainSha(_base) && IsPlainSha(_target)
                ? $"{Short(start)}{separator}{Short(end)}"
                : $"{_base.Name}{separator}{_target.Name} ({shas})";
            Review.RegisterRangeLabel(string.IsNullOrEmpty(start) ? "-R" : start, end, tag);

            _commentTag = tag;
            OnPropertyChanged(nameof(CommentTag));

            _allChanges = changes;
            TotalChanges = changes.Count;
            ViewedCount = changes.Count(c => c.IsViewed);
            RefreshVisible(true);
            IsLoading = false;
        }

        private static bool IsPlainSha(DiffRevision revision)
        {
            return revision.IsWorktree || revision.Name == revision.ShortSha;
        }

        private static string Short(string sha)
        {
            return sha.Length == 0 ? "worktree" : sha[..Math.Min(10, sha.Length)];
        }

        private void RefreshVisible(bool selectFirst)
        {
            if (_allChanges == null)
                return;

            var filter = _searchFilter?.Trim();
            var visible = string.IsNullOrEmpty(filter)
                ? _allChanges
                : _allChanges.Where(c => c.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            VisibleChanges = visible;

            // Keep the open file when it survives (e.g. after a reload or filter change); otherwise start from the first one.
            var openPath = selectFirst ? _restoreFile ?? _changeSelection?.Changes.FirstOrDefault()?.Path : null;
            if (selectFirst)
                _restoreFile = null;
            var keep = openPath == null ? null : visible.FirstOrDefault(c => c.Path == openPath);
            if (keep != null)
                ChangeSelection = new ViewModels.ChangeSelection(new List<Models.Change> { keep });
            else if (selectFirst && visible.Count > 0)
                ChangeSelection = new ViewModels.ChangeSelection(visible.GetRange(0, 1));
            else if (selectFirst)
                ChangeSelection = new ViewModels.ChangeSelection(null);
        }

        private string ViewedKey(Models.Change change)
        {
            return _blobs.TryGetValue(change.Path, out var blob)
                ? $"b:{blob.Old}..{blob.New}|{change.Path}"
                : $"r:{_effectiveBase}|{_effectiveTarget}|{change.Path}";
        }

        private void OnAgentModeChanged(object sender, PropertyChangedEventArgs e)
        {
            // The tab only exists in agent mode, so leave it when agent mode goes away.
            if (e.PropertyName == nameof(AgentMode.IsActive) && !AgentMode.Instance.IsActive && _repo.SelectedViewIndex == 3)
                Dispatcher.UIThread.Post(() => _repo.SelectedViewIndex = 0);
        }

        // A read-only git command whose stdout is wanted as text.
        private class ReadCommand : Commands.Command
        {
            public ReadCommand(string repo, string args)
            {
                WorkingDirectory = repo;
                Context = repo;
                RaiseError = false;
                Args = args;
            }

            public async Task<string> ReadAsync()
            {
                var rs = await ReadToEndAsync().ConfigureAwait(false);
                return rs.IsSuccess ? rs.StdOut : string.Empty;
            }
        }

        // `git diff --raw`: the old and new blob of every changed file, by path.
        private class RawDiffCommand : Commands.Command
        {
            public RawDiffCommand(string repo, string start, string end)
            {
                WorkingDirectory = repo;
                Context = repo;
                RaiseError = false;
                Args = $"diff --raw -z --no-abbrev {start} {end}";
            }

            public async Task<Dictionary<string, (string Old, string New)>> ReadAsync()
            {
                var blobs = new Dictionary<string, (string, string)>();
                var rs = await ReadToEndAsync().ConfigureAwait(false);
                if (!rs.IsSuccess)
                    return blobs;

                // -z output: ":<mode> <mode> <old> <new> <status>" NUL path NUL, with two paths for renames and copies.
                var tokens = rs.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < tokens.Length;)
                {
                    var parts = tokens[i].Split(' ');
                    if (!tokens[i].StartsWith(':') || parts.Length < 5)
                    {
                        i++;
                        continue;
                    }

                    var twoPaths = parts[4][0] is 'R' or 'C';
                    var pathIndex = twoPaths ? i + 2 : i + 1;
                    if (pathIndex < tokens.Length)
                        blobs[tokens[pathIndex]] = (parts[2], parts[3]);

                    i = pathIndex + 1;
                }

                return blobs;
            }
        }

        private const int MaxSavedViewed = 5000;

        private readonly ViewModels.Repository _repo;
        private readonly Dictionary<string, long> _viewed = [];
        private Dictionary<string, (string Old, string New)> _blobs = [];
        private DiffPageState _restoreState;
        private string _restoreFile;
        private bool _initialized;
        private DateTime _requestedAt = DateTime.MinValue;
        private int _loadVersion;
        private DiffRevision _base;
        private DiffRevision _target;
        private bool _useMergeBase;
        private bool _isLoading;
        private string _message;
        private bool _isFileListCollapsed;
        private int _totalChanges;
        private int _viewedCount;
        private bool _isCurrentViewed;
        private string _effectiveBase = string.Empty;
        private string _effectiveTarget = string.Empty;
        private string _commentTag = string.Empty;
        private List<Models.Change> _allChanges;
        private List<Models.Change> _visibleChanges;
        private ViewModels.ChangeSelection _changeSelection = new(null);
        private string _searchFilter = string.Empty;
        private ViewModels.DiffContext _diffContext;
    }
}
