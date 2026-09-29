using System.Text.RegularExpressions;
using System.Xml;

namespace Shared.GameFormats
{
    public static class XmlCompatibilityParser
    {
        private static readonly Regex TrailingDashCommentBlock = new(
            @"(?:(?:\r\n|\n|\r)[ \t]*--[^\r\n]*)+[ \t\r\n]*\z",
            RegexOptions.Compiled);

        private static readonly Regex IncompleteClosingTagLine = new(
            @"(?m)^(?<tag>[ \t]*</(?<name>[A-Za-z_][A-Za-z0-9_.:-]*)[ \t]*)(?=\r?$)",
            RegexOptions.Compiled);

        public static T Parse<T>(
            string content,
            Func<string, T> parser,
            out IReadOnlyList<string> repairs)
        {
            try
            {
                var result = parser(content);
                repairs = Array.Empty<string>();
                return result;
            }
            catch (Exception ex) when (ContainsXmlException(ex))
            {
                var lastFailure = ex;
                var working = content;
                var applied = new List<string>();

                if (TryStripTrailingDashComments(
                        working,
                        out var withoutTrailingComments,
                        out var removedCommentLines))
                {
                    working = withoutTrailingComments;
                    applied.Add(
                        $"Ignored {removedCommentLines} trailing '-- ...' pseudo-comment " +
                        $"line{(removedCommentLines == 1 ? string.Empty : "s")}.");

                    try
                    {
                        var result = parser(working);
                        repairs = applied;
                        return result;
                    }
                    catch (Exception retry) when (ContainsXmlException(retry))
                    {
                        lastFailure = retry;
                    }
                }

                if (TryRepairIncompleteClosingTags(
                        working,
                        out var withClosingTagsRepaired,
                        out var repairedTagNames))
                {
                    working = withClosingTagsRepaired;
                    applied.Add(
                        "Added missing '>' to closing tag" +
                        (repairedTagNames.Count == 1 ? ": " : "s: ") +
                        string.Join(
                            ", ",
                            repairedTagNames.Select(name => $"</{name}>")) +
                        ".");

                    try
                    {
                        var result = parser(working);
                        repairs = applied;
                        return result;
                    }
                    catch (Exception retry) when (ContainsXmlException(retry))
                    {
                        lastFailure = retry;
                    }
                }

                throw lastFailure;
            }
        }

        private static bool TryStripTrailingDashComments(
            string content,
            out string repaired,
            out int removedLineCount)
        {
            var match = TrailingDashCommentBlock.Match(content);
            if (!match.Success)
            {
                repaired = content;
                removedLineCount = 0;
                return false;
            }

            removedLineCount = Regex.Matches(
                    match.Value,
                    @"(?m)^[ \t]*--")
                .Count;
            if (removedLineCount == 0)
            {
                repaired = content;
                return false;
            }

            repaired = content[..match.Index].TrimEnd();
            return true;
        }

        private static bool TryRepairIncompleteClosingTags(
            string content,
            out string repaired,
            out IReadOnlyList<string> repairedTagNames)
        {
            var names = new List<string>();
            repaired = IncompleteClosingTagLine.Replace(
                content,
                match =>
                {
                    var name = match.Groups["name"].Value;
                    names.Add(name);
                    return match.Groups["tag"].Value + ">";
                });

            repairedTagNames = names
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return names.Count != 0;
        }

        private static bool ContainsXmlException(Exception exception)
        {
            for (Exception? current = exception; current != null; current = current.InnerException)
            {
                if (current is XmlException)
                    return true;
            }

            return false;
        }
    }
}
