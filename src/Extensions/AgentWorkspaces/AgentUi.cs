using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace SourceGit.Extensions.AgentWorkspaces
{
    // Display titles and sidebar order of agent workspaces, keyed by their sgws name (the id agents use), so renaming or moving never breaks references.
    // Kept in agent-ui.json beside (not inside) agent-workspaces/, whose *.json files define workspaces.
    public static class AgentUi
    {
        public static string FilePath => Path.Combine(Native.OS.BasicDirectories.ConfigDir, "agent-ui.json");

        public static string TitleOf(string id)
        {
            Load();
            return s_titles.GetValueOrDefault(id, id);
        }

        public static void SetTitle(string id, string title)
        {
            Load();
            if (string.IsNullOrWhiteSpace(title) || title.Trim() == id)
                s_titles.Remove(id);
            else
                s_titles[id] = title.Trim();
            Save();
        }

        public static int IndexOf(string id)
        {
            Load();
            return s_order.IndexOf(id);
        }

        // Ids seen for the first time go on top.
        public static void Track(IEnumerable<string> ids)
        {
            Load();
            var added = false;
            foreach (var id in ids)
            {
                if (s_order.Contains(id))
                    continue;
                s_order.Insert(0, id);
                added = true;
            }

            if (added)
                Save();
        }

        public static void MoveToTop(string id) => MoveBefore(id, s_order.Count > 0 ? s_order[0] : null);

        // Puts id right before beforeId (the end when null).
        public static void MoveBefore(string id, string beforeId)
        {
            Load();
            if (id == beforeId)
                return;

            s_order.Remove(id);
            var index = beforeId == null ? -1 : s_order.IndexOf(beforeId);
            s_order.Insert(index < 0 ? s_order.Count : index, id);
            Save();
        }

        public static void Forget(string id)
        {
            Load();
            s_titles.Remove(id);
            s_order.Remove(id);
            Save();
        }

        private static void Load()
        {
            if (s_loaded)
                return;

            s_loaded = true;
            try
            {
                if (!File.Exists(FilePath))
                    return;

                using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
                if (doc.RootElement.TryGetProperty("titles", out var titles) && titles.ValueKind == JsonValueKind.Object)
                {
                    foreach (var t in titles.EnumerateObject())
                    {
                        if (t.Value.ValueKind == JsonValueKind.String)
                            s_titles[t.Name] = t.Value.GetString();
                    }
                }

                if (doc.RootElement.TryGetProperty("order", out var order) && order.ValueKind == JsonValueKind.Array)
                {
                    foreach (var o in order.EnumerateArray())
                    {
                        if (o.ValueKind == JsonValueKind.String && !s_order.Contains(o.GetString()))
                            s_order.Add(o.GetString());
                    }
                }
            }
            catch
            {
                // A damaged file just means default titles and order.
            }
        }

        private static void Save()
        {
            try
            {
                using var stream = new MemoryStream();
                using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    w.WriteStartObject();
                    w.WriteStartObject("titles");
                    foreach (var (id, title) in s_titles)
                        w.WriteString(id, title);
                    w.WriteEndObject();
                    w.WriteStartArray("order");
                    foreach (var id in s_order)
                        w.WriteStringValue(id);
                    w.WriteEndArray();
                    w.WriteEndObject();
                }

                File.WriteAllText(FilePath + ".tmp", Encoding.UTF8.GetString(stream.ToArray()));
                File.Move(FilePath + ".tmp", FilePath, true);
            }
            catch
            {
                // Retry on the next change.
            }
        }

        private static bool s_loaded;
        private static readonly Dictionary<string, string> s_titles = [];
        private static readonly List<string> s_order = [];
    }
}
