using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SourceGit.Extensions.AgentWorkspaces
{
    // How one side of the Diff tab is restored: refs are resolved again (they may have moved), fixed commits by sha.
    public record RevisionState(string Name, string Spec, string Sha);

    public class DiffPageState
    {
        public RevisionState Base { get; set; }
        public RevisionState Target { get; set; }
        public bool MergeBase { get; set; }
        public string File { get; set; }
        public string Filter { get; set; } = string.Empty;
        public bool Collapsed { get; set; }

        // "b:<old blob>..<new blob>|<path>" -> when it was marked, so a file stays viewed until its own diff changes.
        public Dictionary<string, long> Viewed { get; set; } = [];
    }

    public class RepoState
    {
        public int? Page { get; set; }
        public DiffPageState Diff { get; set; }
    }

    // Everything remembered about one agent workspace: the unsent review and where the user was in each repo.
    public class WorkspaceState
    {
        public string Name { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public List<ReviewComment> Comments { get; set; } = [];
        public string ActiveRepo { get; set; }
        public Dictionary<string, RepoState> Repos { get; set; } = [];
    }

    // One JSON file per workspace in agent-state/ (not agent-workspaces/, whose *.json files define workspaces).
    // A file's last write time is its "last active" time: workspaces that are gone for longer than Retention are deleted.
    public static class AgentState
    {
        public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

        public static string Dir => Path.Combine(Native.OS.BasicDirectories.ConfigDir, "agent-state");

        public static string NormalizeRepo(string path)
        {
            return path.Replace('\\', '/').TrimEnd('/');
        }

        public static WorkspaceState Load(string name)
        {
            if (s_cache.TryGetValue(name, out var cached))
                return cached;

            var state = new WorkspaceState { Name = name };
            var file = FileFor(name);
            if (File.Exists(file))
            {
                try
                {
                    state = Parse(File.ReadAllText(file), name);
                }
                catch
                {
                    // A damaged file just means starting fresh.
                }
            }

            s_cache[name] = state;
            s_written[name] = Serialize(state);
            return state;
        }

        // Writes the state when it changed since the last write.
        public static void Save(WorkspaceState state)
        {
            var json = Serialize(state);
            if (s_written.TryGetValue(state.Name, out var last) && last == json)
                return;

            try
            {
                Directory.CreateDirectory(Dir);
                var file = FileFor(state.Name);
                File.WriteAllText(file + ".tmp", json);
                File.Move(file + ".tmp", file, true);
                s_written[state.Name] = json;
            }
            catch
            {
                // Try again on the next save.
            }
        }

        public static void Delete(string name)
        {
            s_cache.Remove(name);
            s_written.Remove(name);

            try
            {
                File.Delete(FileFor(name));
            }
            catch
            {
                // Already gone or locked; pruning gets it later.
            }
        }

        // Keeps the state of registered workspaces fresh and deletes the state of workspaces that have been gone for Retention.
        public static void Prune(IReadOnlyCollection<string> registered)
        {
            var now = DateTime.UtcNow;
            var names = new HashSet<string>(registered);
            var changed = !names.SetEquals(s_lastNames);

            // A workspace that just disappeared starts its retention period now.
            foreach (var gone in s_lastNames.Except(names))
                Touch(FileFor(gone), now);

            s_lastNames = names;
            if (!changed && now - s_lastPrune < TimeSpan.FromMinutes(10))
                return;

            s_lastPrune = now;
            if (!Directory.Exists(Dir))
                return;

            foreach (var file in Directory.GetFiles(Dir, "*.json"))
            {
                try
                {
                    string name;
                    using (var doc = JsonDocument.Parse(File.ReadAllText(file)))
                        name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;

                    var age = now - File.GetLastWriteTimeUtc(file);
                    if (name != null && names.Contains(name))
                    {
                        if (age > TimeSpan.FromDays(1))
                            Touch(file, now);
                    }
                    else if (age > Retention)
                    {
                        File.Delete(file);
                        if (name != null)
                        {
                            s_cache.Remove(name);
                            s_written.Remove(name);
                        }
                    }
                }
                catch
                {
                    // Ignore files that are being written or are not ours.
                }
            }
        }

        private static void Touch(string file, DateTime now)
        {
            try
            {
                if (File.Exists(file))
                    File.SetLastWriteTimeUtc(file, now);
            }
            catch
            {
                // Ignore.
            }
        }

        private static string FileFor(string name)
        {
            return Path.Combine(Dir, Regex.Replace(name, "[^A-Za-z0-9._-]", "_") + ".json");
        }

        private static string Serialize(WorkspaceState state)
        {
            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteNumber("version", 1);
                w.WriteString("name", state.Name);
                w.WriteString("summary", state.Summary ?? string.Empty);

                w.WriteStartArray("comments");
                foreach (var c in state.Comments)
                {
                    w.WriteStartObject();
                    w.WriteString("repo", c.Repo);
                    w.WriteString("file", c.File);
                    w.WriteString("location", c.Location);
                    w.WriteString("snippet", c.Snippet);
                    w.WriteString("text", c.Text);
                    w.WriteBoolean("isDiff", c.IsDiff);
                    if (c.Selection != null)
                        w.WriteString("selection", c.Selection);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                if (state.ActiveRepo != null)
                    w.WriteString("activeRepo", state.ActiveRepo);

                w.WriteStartObject("repos");
                foreach (var (path, repo) in state.Repos)
                {
                    w.WriteStartObject(path);
                    if (repo.Page.HasValue)
                        w.WriteNumber("page", repo.Page.Value);
                    if (repo.Diff != null)
                        WriteDiff(w, repo.Diff);
                    w.WriteEndObject();
                }
                w.WriteEndObject();

                w.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static void WriteDiff(Utf8JsonWriter w, DiffPageState diff)
        {
            w.WriteStartObject("diff");
            WriteRevision(w, "base", diff.Base);
            WriteRevision(w, "target", diff.Target);
            w.WriteBoolean("mergeBase", diff.MergeBase);
            if (diff.File != null)
                w.WriteString("file", diff.File);
            w.WriteString("filter", diff.Filter ?? string.Empty);
            w.WriteBoolean("collapsed", diff.Collapsed);

            w.WriteStartObject("viewed");
            foreach (var (key, at) in diff.Viewed.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal))
                w.WriteNumber(key, at);
            w.WriteEndObject();

            w.WriteEndObject();
        }

        private static void WriteRevision(Utf8JsonWriter w, string property, RevisionState revision)
        {
            if (revision == null)
                return;

            w.WriteStartObject(property);
            w.WriteString("name", revision.Name);
            if (revision.Spec != null)
                w.WriteString("spec", revision.Spec);
            w.WriteString("sha", revision.Sha ?? string.Empty);
            w.WriteEndObject();
        }

        private static WorkspaceState Parse(string json, string name)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var state = new WorkspaceState { Name = name };

            state.Summary = Str(root, "summary") ?? string.Empty;
            if (root.TryGetProperty("comments", out var comments) && comments.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in comments.EnumerateArray())
                {
                    state.Comments.Add(new ReviewComment(
                        Str(c, "repo") ?? string.Empty,
                        Str(c, "file") ?? string.Empty,
                        Str(c, "location") ?? string.Empty,
                        Str(c, "snippet") ?? string.Empty,
                        Str(c, "text") ?? string.Empty,
                        !c.TryGetProperty("isDiff", out var isDiff) || isDiff.ValueKind != JsonValueKind.False,
                        Str(c, "selection")));
                }
            }

            state.ActiveRepo = Str(root, "activeRepo");
            if (root.TryGetProperty("repos", out var repos) && repos.ValueKind == JsonValueKind.Object)
            {
                foreach (var repo in repos.EnumerateObject())
                {
                    var rs = new RepoState();
                    if (repo.Value.TryGetProperty("page", out var page) && page.TryGetInt32(out var idx))
                        rs.Page = idx;
                    if (repo.Value.TryGetProperty("diff", out var diff) && diff.ValueKind == JsonValueKind.Object)
                        rs.Diff = ParseDiff(diff);
                    state.Repos[repo.Name] = rs;
                }
            }

            return state;
        }

        private static DiffPageState ParseDiff(JsonElement e)
        {
            var diff = new DiffPageState
            {
                Base = ParseRevision(e, "base"),
                Target = ParseRevision(e, "target"),
                MergeBase = e.TryGetProperty("mergeBase", out var mb) && mb.ValueKind == JsonValueKind.True,
                File = Str(e, "file"),
                Filter = Str(e, "filter") ?? string.Empty,
                Collapsed = e.TryGetProperty("collapsed", out var col) && col.ValueKind == JsonValueKind.True,
            };

            if (e.TryGetProperty("viewed", out var viewed) && viewed.ValueKind == JsonValueKind.Object)
            {
                foreach (var v in viewed.EnumerateObject())
                    diff.Viewed[v.Name] = v.Value.TryGetInt64(out var at) ? at : 0;
            }

            return diff;
        }

        private static RevisionState ParseRevision(JsonElement e, string property)
        {
            if (!e.TryGetProperty(property, out var r) || r.ValueKind != JsonValueKind.Object)
                return null;

            return new RevisionState(Str(r, "name") ?? string.Empty, Str(r, "spec"), Str(r, "sha") ?? string.Empty);
        }

        private static string Str(JsonElement e, string property)
        {
            return e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }

        private static readonly Dictionary<string, WorkspaceState> s_cache = [];
        private static readonly Dictionary<string, string> s_written = [];
        private static HashSet<string> s_lastNames = [];
        private static DateTime s_lastPrune = DateTime.MinValue;
    }
}
