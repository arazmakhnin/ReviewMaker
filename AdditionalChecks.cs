using System;
using System.Collections.Generic;
using Atlassian.Jira;

namespace ReviewMaker
{
    public static class AdditionalChecks
    {
        public static IReadOnlyCollection<string> Make(Issue issue)
        {
            var failedChecks = new List<string>();

            CheckIssueType(issue, failedChecks);

            return failedChecks;
        }

        private static void CheckIssueType(Issue issue, ICollection<string> failedChecks)
        {
            var summaryParts = issue.Summary.Split("::");
            var summaryTicketType = summaryParts[0].Trim();

            if (summaryTicketType.Equals(issue.Type.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (issue.Type == "Dead Code" && summaryTicketType.Equals("DeadCode", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (issue.Type == "Symbolic Execution - Memory Leaks"
                && (summaryTicketType.Equals("Memory Leaks", StringComparison.OrdinalIgnoreCase) ||
                    summaryTicketType.Equals("Memory Leak", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            if (issue.Type == "BRP Issues")
            {
                if (CheckBrpIssueType(summaryParts[0].Trim()) ||
                    summaryParts.Length > 1 && CheckBrpIssueType(summaryParts[0].Trim() + summaryParts[1].Trim()))
                {
                    return;
                }
            }

            failedChecks.Add($"Ticket has type \"{issue.Type}\", but there is \"{summaryTicketType}\" in ticket summary");
        }

        private static bool CheckBrpIssueType(string summaryTicketType)
        {
            var simplifiedSummary = summaryTicketType
                .Replace(" ", string.Empty)
                .Replace("-", string.Empty);

            return simplifiedSummary.Equals("BrpFormatting", StringComparison.OrdinalIgnoreCase)
                   || simplifiedSummary.Equals("BrpMagicStrings", StringComparison.OrdinalIgnoreCase)
                   || simplifiedSummary.Equals("BrpIssuesFormatting", StringComparison.OrdinalIgnoreCase)
                   || simplifiedSummary.Equals("BrpIssuesMagicStrings", StringComparison.OrdinalIgnoreCase);
        }
    }
}