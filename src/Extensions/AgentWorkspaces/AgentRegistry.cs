using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Media;
using Avalonia.Threading;

namespace SourceGit.Extensions.AgentWorkspaces
{
    // Loads agent-workspaces/*.json ({"name", "repos", "color"?, "socket"?, "focus"?}) as in-memory workspaces, never saved to preferences.
    public static class AgentRegistry
    {
        public static event Action Changed;

        public static string Dir => Path.Combine(Native.OS.BasicDirectories.ConfigDir, "agent-workspaces");

        public static List<ViewModels.Workspace> Workspaces { get; private set; } = [];

        public static string GetSocket(ViewModels.Workspace workspace)
        {
            return s_sockets.GetValueOrDefault(workspace);
        }

        public static ViewModels.Workspace TakeFocusRequest()
        {
            var workspace = s_focusRequest;
            s_focusRequest = null;
            return workspace;
        }

        public static bool IsAgentWorkspace(ViewModels.Workspace workspace)
        {
            return Workspaces.Contains(workspace);
        }

        public static void Start()
        {
            if (s_watcher != null)
                return;

            Directory.CreateDirectory(Dir);

            s_debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            s_debounce.Tick += (_, _) =>
            {
                s_debounce.Stop();
                Sync();
            };

            s_watcher = new FileSystemWatcher(Dir, "*.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            s_watcher.Created += OnFileChanged;
            s_watcher.Changed += OnFileChanged;
            s_watcher.Deleted += OnFileChanged;
            s_watcher.Renamed += OnFileChanged;

            Sync();
        }

        private static void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                s_debounce.Stop();
                s_debounce.Start();
            });
        }

        private static void Sync()
        {
            var launcher = App.GetLauncher();
            var workspaces = new List<ViewModels.Workspace>();
            var sockets = new Dictionary<ViewModels.Workspace, string>();
            var isFirstSync = !s_synced;
            var newestFocus = 0L;

            foreach (var (name, entry) in ReadEntries())
            {
                var workspace = Workspaces.Find(w => w.Name == name) ?? new ViewModels.Workspace { Name = name, Color = DefaultColor(name) };
                if (entry.Color.HasValue)
                    workspace.Color = entry.Color.Value;

                if (launcher?.ActiveWorkspace == workspace)
                {
                    var activePage = launcher.ActivePage;
                    foreach (var repo in entry.Repos.Except(workspace.Repositories))
                        launcher.OpenRepositoryInTab(repo, null);
                    launcher.ActivePage = activePage;
                }
                else
                {
                    workspace.Repositories = entry.Repos;
                }

                workspaces.Add(workspace);
                if (!string.IsNullOrEmpty(entry.Socket))
                    sockets[workspace] = entry.Socket;

                // On startup only honor recent requests, so a restart doesn't jump into an old workspace.
                var lastFocus = s_lastFocus.GetValueOrDefault(name, isFirstSync ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60_000 : 0);
                if (entry.Focus > lastFocus && entry.Focus > newestFocus)
                {
                    newestFocus = entry.Focus;
                    s_focusRequest = workspace;
                }
                s_lastFocus[name] = Math.Max(lastFocus, entry.Focus);
            }

            Workspaces = workspaces;
            s_sockets = sockets;
            s_synced = true;
            Changed?.Invoke();
        }

        private static List<(string, Entry)> ReadEntries()
        {
            var entries = new List<(string, Entry)>();
            foreach (var file in Directory.GetFiles(Dir, "*.json").Order())
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var root = doc.RootElement;
                    var name = root.TryGetProperty("name", out var n) ? n.GetString() : Path.GetFileNameWithoutExtension(file);
                    var repos = root.GetProperty("repos").EnumerateArray()
                        .Select(r => r.GetString()?.Replace('\\', '/').TrimEnd('/'))
                        .Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r))
                        .Distinct()
                        .ToList();

                    var socket = root.TryGetProperty("socket", out var so) ? so.GetString() : null;
                    var focus = root.TryGetProperty("focus", out var f) && f.TryGetInt64(out var ms) ? ms : 0;

                    uint? color = null;
                    if (root.TryGetProperty("color", out var c) && Color.TryParse(c.GetString(), out var parsed))
                        color = parsed.ToUInt32();

                    if (!string.IsNullOrWhiteSpace(name) && repos.Count > 0 && entries.All(e => e.Item1 != name))
                        entries.Add((name, new Entry(repos, color, socket, focus)));
                }
                catch
                {
                    // Ignore partially written or malformed files.
                }
            }

            return entries;
        }

        private static uint DefaultColor(string name)
        {
            uint[] palette = [0xFF4E9A06, 0xFFC4A000, 0xFF3465A4, 0xFF75507B, 0xFFCE5C00, 0xFF06989A, 0xFFCC0000];
            var hash = name.Aggregate(0u, (h, ch) => h * 31 + ch);
            return palette[hash % palette.Length];
        }

        private record Entry(List<string> Repos, uint? Color, string Socket, long Focus);

        private static FileSystemWatcher s_watcher;
        private static DispatcherTimer s_debounce;
        private static Dictionary<ViewModels.Workspace, string> s_sockets = [];
        private static readonly Dictionary<string, long> s_lastFocus = [];
        private static ViewModels.Workspace s_focusRequest;
        private static bool s_synced;
    }
}
