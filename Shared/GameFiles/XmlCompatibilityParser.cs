using System.Text.RegularExpressions;
using System.Xml;

namespace Shared.GameFormats
{
    public static class XmlCompatibilityParser
    {
        private static readonly Regex RootElementStart = new(
            @"\A\s*(?:<\?xml\b.*?\?>\s*)?(?:<!--.*?-->\s*)*<(?<name>[A-Za-z_][A-Za-z0-9_.:-]*)(?:\s|>)",
            RegexOptions.Compiled | RegexOptions.Singleline);

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

                if (TryStripTrailingTextAfterClosedRoot(
                        working,
                        parser,
                        out var withoutTrailingText,
                        out var trailingDescription,
                        out var parsedWithoutTrailingText))
                {
                    applied.Add(trailingDescription);
                    repairs = applied;
                    return parsedWithoutTrailingText;
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

                    if (TryStripTrailingTextAfterClosedRoot(
                            working,
                            parser,
                            out withoutTrailingText,
                            out trailingDescription,
                            out parsedWithoutTrailingText))
                    {
                        applied.Add(trailingDescription);
                        repairs = applied;
                        return parsedWithoutTrailingText;
                    }
                }

                throw lastFailure;
            }
        }

        private static bool TryStripTrailingTextAfterClosedRoot<T>(
            string content,
            Func<string, T> parser,
            out string repaired,
            out string description,
            out T result)
        {
            repaired = content;
            description = string.Empty;
            result = default!;

            var rootMatch = RootElementStart.Match(content);
            if (!rootMatch.Success)
                return false;

            var rootName = rootMatch.Groups["name"].Value;
            var closingTag = new Regex(
                $@"</{Regex.Escape(rootName)}\s*>",
                RegexOptions.IgnoreCase);
            var matches = closingTag.Matches(content);
            if (matches.Count == 0)
                return false;

            var lastClose = matches[^1];
            var documentEnd = lastClose.Index + lastClose.Length;
            var suffix = content[documentEnd..];
            if (string.IsNullOrWhiteSpace(suffix))
                return false;

            var candidate = content[..documentEnd];
            try
            {
                result = parser(candidate);
            }
            catch (Exception ex) when (ContainsXmlException(ex))
            {
                return false;
            }

            repaired = candidate;
            var nonEmptyLines = Regex.Matches(
                    suffix,
                    @"(?m)^\s*\S.*$")
                .Count;
            description =
                $"Ignored trailing non-XML text after </{rootName}> " +
                $"({Math.Max(1, nonEmptyLines)} non-empty line" +
                $"{(Math.Max(1, nonEmptyLines) == 1 ? string.Empty : "s")}).";
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
