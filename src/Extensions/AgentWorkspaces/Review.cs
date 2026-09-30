using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SourceGit.Extensions.AgentWorkspaces
{
    public record ReviewComment(string Repo, string File, string Location, string Snippet, string Text, bool IsDiff = true)
    {
        public string Title => string.IsNullOrEmpty(File) ? $"{Path.GetFileName(Repo)}{Location}" : $"{Path.GetFileName(Repo)}/{File}{Location}";
    }

    public static class Review
    {
        public static ReviewComment CreateDraft(string repo, Models.DiffOption option, List<Models.TextDiffLine> selected)
        {
            var newLines = selected.Where(l => l.NewLineNumber > 0).Select(l => l.NewLineNumber).ToList();
            var oldLines = selected.Where(l => l.OldLineNumber > 0).Select(l => l.OldLineNumber).ToList();
            string range;
            if (newLines.Count == 0 && oldLines.Count == 0)
                range = string.Empty;
            else if (newLines.Count == 0)
                range = $":{FormatRange(oldLines)} (old)";
            else
            {
                range = $":{FormatRange(newLines)}";
                if (oldLines.Count > 0 && FormatRange(oldLines) != FormatRange(newLines))
                    range += $" (was {FormatRange(oldLines)})";
            }

            if (!option.IsLocalChange)
                range += " @ " + string.Join("..", option.Revisions.Select(Short));

            var snippet = new StringBuilder();
            foreach (var line in selected.Take(60))
            {
                var prefix = line.Type switch
                {
                    Models.TextDiffLineType.Added => '+',
                    Models.TextDiffLineType.Deleted => '-',
                    _ => ' ',
                };
                snippet.Append(prefix).Append(line.Content).Append('\n');
            }

            return new ReviewComment(repo, option.Path, range, snippet.ToString().TrimEnd('\n'), string.Empty);
        }

        public static ReviewComment CreateFileDraft(string repo, List<Models.Change> changes)
        {
            var file = changes.Count == 1 ? changes[0].Path : string.Empty;
            var location = changes.Count == 1 ? string.Empty : $" ({changes.Count} files)";
            var snippet = string.Join('\n', changes.Select(c => c.Path));
            return new ReviewComment(repo, file, location, snippet, string.Empty, false);
        }

        public static string Format(IReadOnlyList<ReviewComment> comments, string summary)
        {
            var builder = new StringBuilder("[SourceGit review]\n");
            if (!string.IsNullOrWhiteSpace(summary))
                builder.Append("\n# Overall note:\n").Append(summary.Trim()).Append('\n');

            if (comments.Count > 0)
                builder.Append("\n# Comments:\n");

            for (var i = 0; i < comments.Count; i++)
            {
                var c = comments[i];
                if (i > 0)
                    builder.Append('\n');
                builder.Append(i + 1).Append(". ").Append(c.Title).Append('\n');
                if (c.IsDiff || string.IsNullOrEmpty(c.File))
                    builder.Append(c.IsDiff ? "```diff\n" : "```\n").Append(c.Snippet).Append("\n```\n");
                builder.Append(c.Text.Trim()).Append('\n');
            }

            return builder.ToString().TrimEnd('\n');
        }

        public static async Task SendAsync(string socketPath, string text)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "user");
                writer.WriteStartObject("message");
                writer.WriteString("role", "user");
                writer.WriteString("content", text);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            stream.WriteByte((byte)'\n');

            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
            await socket.SendAsync(stream.ToArray(), SocketFlags.None);
            socket.Shutdown(SocketShutdown.Send);
        }

        private static string FormatRange(List<int> numbers)
        {
            var min = numbers.Min();
            var max = numbers.Max();
            return min == max ? $"{min}" : $"{min}-{max}";
        }

        private static string Short(string revision)
        {
            return revision.Length > 10 ? revision[..10] : revision;
        }
    }
}
