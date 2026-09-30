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
        public string Title => string.IsNullOrEmpty(File) ? $"{Path.GetFileName(Repo)} · {Location}" : $"{Path.GetFileName(Repo)}/{File} · {Location}";
    }

    public static class Review
    {
        public static ReviewComment CreateDraft(string repo, Models.DiffOption option, List<Models.TextDiffLine> selected)
        {
            var newLines = selected.Where(l => l.NewLineNumber > 0).Select(l => l.NewLineNumber).ToList();
            var oldLines = selected.Where(l => l.OldLineNumber > 0).Select(l => l.OldLineNumber).ToList();
            var range = newLines.Count > 0
                ? FormatRange("line", newLines)
                : FormatRange("old line", oldLines);

            string context;
            if (option.IsLocalChange)
                context = option.IsUnstaged ? "unstaged" : "staged";
            else if (option.Revisions.Count == 2)
                context = $"{Short(option.Revisions[0])}..{Short(option.Revisions[1])}";
            else
                context = string.Join("..", option.Revisions.Select(Short));

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

            return new ReviewComment(repo, option.Path, $"{range} ({context})", snippet.ToString().TrimEnd('\n'), string.Empty);
        }

        public static ReviewComment CreateFileDraft(string repo, List<Models.Change> changes, bool isUnstaged)
        {
            var area = isUnstaged ? "unstaged" : "staged";
            var file = changes.Count == 1 ? changes[0].Path : string.Empty;
            var location = changes.Count == 1 ? $"whole file ({area})" : $"{changes.Count} files ({area})";
            var snippet = string.Join('\n', changes.Select(c => c.Path));
            return new ReviewComment(repo, file, location, snippet, string.Empty, false);
        }

        public static string Format(string workspace, IReadOnlyList<ReviewComment> comments, string summary)
        {
            var builder = new StringBuilder();
            builder.Append("[SourceGit review] The user reviewed your changes in SourceGit (workspace \"").Append(workspace)
                .Append("\") and submitted ").Append(comments.Count).Append(comments.Count == 1 ? " comment" : " comments")
                .Append(" on the diff. Please address each one.\n");

            for (var i = 0; i < comments.Count; i++)
            {
                var c = comments[i];
                var target = string.IsNullOrEmpty(c.File) ? c.Repo : Path.Combine(c.Repo, c.File);
                builder.Append("\n## ").Append(i + 1).Append(". ").Append(target).Append(" — ").Append(c.Location).Append('\n');
                builder.Append(c.IsDiff ? "```diff\n" : "```\n").Append(c.Snippet).Append("\n```\n");
                builder.Append(c.Text.Trim()).Append('\n');
            }

            if (!string.IsNullOrWhiteSpace(summary))
                builder.Append("\n## Overall\n").Append(summary.Trim()).Append('\n');

            return builder.ToString();
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

        private static string FormatRange(string label, List<int> numbers)
        {
            if (numbers.Count == 0)
                return "context";

            var min = numbers.Min();
            var max = numbers.Max();
            return min == max ? $"{label} {min}" : $"{label}s {min}-{max}";
        }

        private static string Short(string revision)
        {
            return revision.Length > 10 ? revision[..10] : revision;
        }
    }
}
