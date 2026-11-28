using System;
using System.IO;
using System.Windows;
using System.Windows.Documents;

namespace SoulLink.Views
{
    internal static class ReleaseNotesDocument
    {
        internal static string ReadHistory()
        {
            using var stream = typeof(ReleaseNotesDocument).Assembly.GetManifestResourceStream("SoulLink.ReleaseNotes.md");
            if (stream == null) return "";
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        internal static FlowDocument Render(string? markdown, string emptyMessage)
        {
            var document = new FlowDocument { PagePadding = new Thickness(0), FontSize = 12.5, LineHeight = 20 };
            document.SetResourceReference(TextElement.FontFamilyProperty, "UiFont");
            document.SetResourceReference(TextElement.ForegroundProperty, "TextSecondary");

            using var reader = new StringReader(string.IsNullOrWhiteSpace(markdown) ? emptyMessage : markdown);
            while (reader.ReadLine() is { } raw)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line == "# 更新说明") continue;
                bool heading = line.StartsWith("## ", StringComparison.Ordinal) || line.StartsWith("### ", StringComparison.Ordinal);
                bool bullet = line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal);
                if (heading) line = line.Substring(line.IndexOf(' ') + 1);
                if (bullet) line = line.Substring(2);

                var paragraph = new Paragraph { Margin = new Thickness(0, heading && document.Blocks.Count > 0 ? 12 : 0, 0, heading ? 5 : 3) };
                if (heading)
                {
                    paragraph.FontSize = 14;
                    paragraph.FontWeight = FontWeights.SemiBold;
                    paragraph.SetResourceReference(TextElement.ForegroundProperty, "TextPrimary");
                }
                if (bullet)
                {
                    paragraph.Padding = new Thickness(12, 0, 0, 0);
                    paragraph.TextIndent = -12;
                    paragraph.Inlines.Add(new Run("•  "));
                }
                AddText(paragraph, line);
                document.Blocks.Add(paragraph);
            }
            return document;
        }

        private static void AddText(Paragraph paragraph, string value)
        {
            var segments = value.Split("**", StringSplitOptions.None);
            if (segments.Length % 2 == 0)
            {
                paragraph.Inlines.Add(new Run(value));
                return;
            }
            for (int i = 0; i < segments.Length; i++)
                paragraph.Inlines.Add(new Run(segments[i]) { FontWeight = i % 2 == 1 ? FontWeights.SemiBold : paragraph.FontWeight });
        }
    }
}
