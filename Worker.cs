using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Atlassian.Jira;
using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using Newtonsoft.Json.Linq;
using ReviewMaker.Exceptions;
using ReviewMaker.Helpers;

namespace ReviewMaker
{
    public class Worker
    {
        private static readonly Regex JiraUrlRegex = new Regex(@"^https://jira\.devfactory\.com/browse/(CC-\d+)$");
        private static readonly Regex JiraKeyRegex = new Regex(@"^(CC-\d+)$");

        private readonly Jira _jira;
        private readonly JiraUser _jiraUser;
        private readonly DriveService _driveService;
        private readonly SheetsService _sheetsService;
        private readonly Settings _settings;
        private readonly IDictionary<string, string> _googleDriveFolders;
        private Issue _issue;
        private JiraUser _prAuthor;
        private File _qbFile;
        private PrInfo _prInfo;

        public Worker(
            Jira jira, 
            JiraUser jiraUser, 
            DriveService driveService, 
            SheetsService sheetsService,
            Settings settings,
            IDictionary<string, string> googleDriveFolders)
        {
            _jira = jira;
            _jiraUser = jiraUser;
            _driveService = driveService;
            _sheetsService = sheetsService;
            _settings = settings;
            _googleDriveFolders = googleDriveFolders;
        }

        public async Task DoWork()
        {
            //GithubHelper.ParsePrUrl("https://github.com/trilogy-group/km-all-projects/pull/6963", out _prInfo);
            //await ReviewPr("<It's a qb link>", true, new List<string>
            //{
            //    "1.1. One breaked rule",
            //    "2.5. Another breaked rule"
            //});
            //return;

            try
            {
                Console.Write("Enter jira ticket or jira issue key to start review: ");
                var ticketUrl = Console.ReadLine() ?? string.Empty;

                var dateTime = _settings.UseLocalDate ? DateTime.Now : DateTime.UtcNow;
                var date = dateTime.ToString("yyyy-MM-dd HH:mm");

                await StartReview(date, ticketUrl);

                Console.WriteLine();
                var qbLink = $"https://docs.google.com/spreadsheets/d/{_qbFile.Id}/edit#gid=1247094356";
                Console.WriteLine($"QB url: {qbLink}");

                await FinishReview(date, qbLink);
            }
            catch (ReviewException ex)
            {
                ConsoleHelper.WriteLineColor(ex.Message, ConsoleColor.Red);
            }
        }

        private async Task StartReview(string date, string ticketUrl)
        {
            ticketUrl = ticketUrl.Trim();
            var match = JiraUrlRegex.Match(ticketUrl);
            if (!match.Success)
            {
                match = JiraKeyRegex.Match(ticketUrl);
                if (!match.Success)
                {
                    throw new ReviewException("Unknown jira ticket or jira issue key");
                }

                ticketUrl = $"https://jira.devfactory.com/browse/{ticketUrl}";
            }

            var issueKey = match.Groups[1].Value;

            Console.Write("Get ticket... ");
            _issue = await _jira.Issues.GetIssueAsync(issueKey);
            if (string.IsNullOrWhiteSpace(_issue.Assignee))
            {
                Console.WriteLine("done");
                throw new ReviewException("Assignee is empty");
            }

            _prAuthor = await _jira.Users.GetUserAsync(_issue.Assignee);
            Console.WriteLine("done");

            var issueStatus = _issue.Status.ToString();
            if (issueStatus != "Ready For Review" && issueStatus != "Code Review")
            {
                throw new ReviewException($"Unknown ticket state: {_issue.Status}");
            }

            Console.Write("Make checks... ");
            var failedChecks = AdditionalChecks.Make(_issue);
            Console.WriteLine("done");

            if (failedChecks.Any())
            {
                ConsoleHelper.WriteLineColor("Failed checks: ", ConsoleColor.Yellow);

                foreach (var failedCheck in failedChecks)
                {
                    ConsoleHelper.WriteLineColor(" * " + failedCheck, ConsoleColor.Yellow);
                }
            }

            Console.Write("Set peer reviewer... ");
            if (_issue.CustomFields["Peer reviewer"] == null)
            {
                _issue.CustomFields.Add("Peer reviewer", _jiraUser.Username);
                await _jira.Issues.UpdateIssueAsync(_issue);

                // Need to reload ticket. Otherwise we can't move it forward to the next status
                _issue = await _jira.Issues.GetIssueAsync(issueKey);
                Console.WriteLine("done");
            }
            else
            {
                var reviewer = _issue.CustomFields["Peer reviewer"].Values.First();
                Console.WriteLine($"already set to {reviewer}");
                if (reviewer != _jiraUser.Username)
                {
                    throw new ReviewException($"Ticket is already assigned to {reviewer}");
                }
            }

            if (issueStatus == "Ready For Review")
            {
                Console.Write("Start review... ");
                await _issue.WorkflowTransitionAsync("Start Review");
                Console.WriteLine("done");
            }
            
            var prUrl = _issue["Code Review Ticket URL"].ToString();
            if (!GithubHelper.ParsePrUrl(prUrl, out _prInfo))
            {
                throw new ReviewException($"Invalid PR url: {prUrl}");
            }

            var qbFolder = _prInfo.RepoName;
            var qbFileName = $"{_issue.Key} {date}";
            if (!_googleDriveFolders.ContainsKey(qbFolder))
            {
                Console.Write("Get QB folder id... ");
                var qbFolderId = GetQbFolderId(qbFolder);
                _googleDriveFolders.Add(qbFolder, qbFolderId);
                Console.WriteLine("done");
            }

            Console.Write("Create QB file... ");
            _qbFile = CreateQbFile(qbFolder, qbFileName);
            Console.WriteLine("done");

            Console.Write("Fill QB file... ");
            FillQbSummary(ticketUrl, prUrl, _prAuthor.DisplayName);
            FillQbSheets(_issue.Type.ToString());
            Console.WriteLine("done");
        }

        private string GetQbFolderId(string qbFolder)
        {
            const string parentQaFolder = "1o38nUKG95A6zqC90L05BjfIx28eCCf1m";

            var listRequest = _driveService.Files.List();
            listRequest.Q = $"name = '{qbFolder}' and '{parentQaFolder}' in parents";
            var listResult = listRequest.Execute();

            if (listResult.Files.Count > 1)
            {
                throw new ReviewException($"Found multiple folders with the same name: {qbFolder}. Ids:\r\n" +
                                          string.Join("\r\n", listResult.Files.Select(f => f.Id)));
            }

            if (listResult.Files.Count == 1)
            {
                return listResult.Files.Single().Id;
            }

            var file = new File
            {
                MimeType = "application/vnd.google-apps.folder",
                Name = qbFolder,
                Parents = new List<string>
                {
                    parentQaFolder
                }
            };
            var createRequest = _driveService.Files.Create(file);
            var createResult = createRequest.Execute();

            return createResult.Id;
        }

        private File CreateQbFile(string qbFolder, string qbFileName)
        {
            var copy = new File
            {
                Parents = new List<string> { _googleDriveFolders[qbFolder] },
                CopyRequiresWriterPermission = true,
                Name = qbFileName,
            };

            const string templateId = "1zxyseum3dOcfZsk0QLdWsX3Y2F6c8yi-cFHVMTGSV1I";
            var copyRequest = _driveService.Files.Copy(copy, templateId);
            var file = copyRequest.Execute();
            return file;
        }

        private void FillQbSummary(string issueUrl, string prUrl, string prAuthor)
        {
            var reviewer = _jiraUser.DisplayName;
            var summary1 = _sheetsService.Spreadsheets.Values.Update(
                new ValueRange
                {
                    Values = new List<IList<object>>
                    {
                        new List<object> { issueUrl },
                        new List<object> { prUrl },
                        new List<object> { reviewer }
                    }
                },
                _qbFile.Id,
                "Summary!B7:B9");

            summary1.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.RAW;
            summary1.Execute();

            var summary2 = _sheetsService.Spreadsheets.Values.Update(
                new ValueRange
                {
                    Values = new List<IList<object>>
                    {
                        new List<object> { prAuthor }
                    }
                },
                _qbFile.Id,
                "Summary!B11:B11");

            summary2.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.RAW;
            summary2.Execute();
        }

        private void FillQbSheets(string issueType)
        {
            foreach (var page in _settings.SheetPages.Where(p => p.RulesCount > 0))
            {
                if (!string.IsNullOrWhiteSpace(page.TicketTypeCondition) && issueType != page.TicketTypeCondition)
                {
                    continue;
                }

                var data = new ValueRange
                {
                    Values = Enumerable
                        .Repeat((IList<object>)new List<object> { "PASS" }, page.RulesCount)
                        .ToList()
                };
                //        new List<IList<object>>
                //    {
                //        new List<object> { "PASS" },
                //        new List<object> { "PASS" },
                //        new List<object> { "PASS" },
                //        new List<object> { "PASS" },
                //        new List<object> { "PASS" },
                //        new List<object> { "PASS" },
                //        new List<object> { "PASS" },
                //        new List<object> { "PASS" },
                //        new List<object> { "PASS" },
                //        new List<object> { "PASS" },
                //    }
                //}
                var pass1 = _sheetsService.Spreadsheets.Values.Update(
                    data,
                    _qbFile.Id,
                    $"{page.Name}!A3:A{(page.RulesCount + 2)}");

                pass1.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.RAW;
                pass1.Execute();
            }
        }

        private async Task FinishReview(string date, string qbLink)
        {
            while (true)
            {
                Console.Write("Please enter command to finish review [open qb (o), approve (a), reject (r) or skip this PR (s)]: ");
                var strCommand = Console.ReadLine() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(strCommand))
                {
                    continue;
                }

                try
                {
                    switch (strCommand.ToLowerInvariant())
                    {
                        case "open qb":
                        case "open":
                        case "o":
                            OpenQbLink(qbLink);
                            continue;

                        case "approve":
                        case "a":
                            await Approve(qbLink);
                            return;

                        case "reject":
                        case "r":
                            await Reject(date, qbLink);
                            return;

                        case "skip":
                        case "s":
                            return;

                        default:
                            Console.WriteLine($"Unknown command: {strCommand}");
                            continue;
                    }
                }
                catch (ReviewException ex)
                {
                    ConsoleHelper.WriteLineColor(ex.Message, ConsoleColor.Red);
                }
            }
        }

        private void OpenQbLink(string qbLink)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd",
                    Arguments = "/c start " + qbLink,
                    UseShellExecute = true,
                    CreateNoWindow = true
                });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", qbLink);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", qbLink);
            }
        }

        private async Task Approve(string qbLink)
        {
            // Check qb summary passed
            // Move ticket to "Code merge"
            // Approve PR

            Console.Write("Check QB file... ");
            var qbResult = GetQbResult();
            if (qbResult.Equals("Passed", StringComparison.OrdinalIgnoreCase))
            {
                ConsoleHelper.WriteLineColor("passed", ConsoleColor.Green);
            }
            else
            {
                ConsoleHelper.WriteLineColor(qbResult, ConsoleColor.Red);
            }

            Console.Write("Move ticket to \"Code merge\"... ");
            _issue["QB Checklist Report"] = qbLink;
            await _issue.WorkflowTransitionAsync("Code review approved");
            Console.WriteLine("done");

            if (_settings.ApprovePrAction == "approve" || _settings.ApprovePrAction == "comment")
            {
                Console.Write(_settings.ApprovePrAction == "approve" ? "Approve PR... " : "Leave a comment... ");
                if (await ReviewPr(qbLink, true, null))
                {
                    Console.WriteLine("done");
                }
                else
                {
                    ConsoleHelper.WriteLineColor("error", ConsoleColor.Red);
                }
            }
            else
            {
                ConsoleHelper.WriteLineColor(
                    $"Unknown PR approve action: \"{_settings.ApprovePrAction}\". PR wasn't approved. Please do it manually", 
                    ConsoleColor.Yellow);
            }
        }

        private async Task<bool> ReviewPr(string qbLink, bool approve, IReadOnlyCollection<string> failedItems)
        {
            // Get user login
            var loginQuery = @"query { viewer { login }}";
            var loginData = await GithubHelper.Query(loginQuery, _settings.GithubToken);
            var login = loginData["viewer"]["login"].Value<string>();


            // Get pending review
            var pendingReviewQuery = @"query q { 
  repository(owner:""" + _prInfo.OwnerName + @""", name:""" + _prInfo.RepoName + @""") {
            pullRequest(number: " + _prInfo.PrNumber + @") {
                id
                reviews(last: 20, author: """ + login + @""") {
                    nodes{
                        id
                        state
                    }
                }
            }
        }
    }";
            var pendingReviewData = await GithubHelper.Query(pendingReviewQuery, _settings.GithubToken);
            var pendingReviews = pendingReviewData["repository"]["pullRequest"]["reviews"]["nodes"] as JArray;
            var pendingReview = pendingReviews?.FirstOrDefault(r => r["state"].Value<string>() == "PENDING");

            string reviewId;
            if (pendingReview == null)
            {
                var prId = pendingReviewData["repository"]["pullRequest"]["id"].Value<string>();

                var addReviewMutation = @"mutation w{ 
                addPullRequestReview(input: {pullRequestId: """ + prId + @"""}) { 
                    pullRequestReview {id}
                }
            }";

                var reviewData = await GithubHelper.Query(addReviewMutation, _settings.GithubToken);
                reviewId = reviewData["addPullRequestReview"]["pullRequestReview"]["id"].Value<string>();
            }
            else
            {
                reviewId = pendingReview["id"].Value<string>();
            }

            string reviewBody;
            string reviewEvent;
            if (approve)
            {
                if (_settings.ApprovePrAction == "approve")
                {
                    reviewBody = $"body: \"QB: {qbLink}\"";
                    reviewEvent = "APPROVE";
                }
                else
                {
                    reviewBody = $"body: \"Approved\\r\\nQB: {qbLink}\"";
                    reviewEvent = "COMMENT";
                }
            }
            else
            {
                reviewEvent = "REQUEST_CHANGES";
                var failedItemsText = string.Join("\\r\\n", failedItems.Select(i => $"**{i}**"));
                reviewBody = $"body: \"{failedItemsText}\\r\\nQB: {qbLink}\"";
            }

            var submitReviewMutation = @"mutation e { 
                submitPullRequestReview(input: {
                    " + reviewBody + @"
                    pullRequestReviewId: """ + reviewId + @""",
                    event: " + reviewEvent + @"}) { 
                        clientMutationId
                }
            }";

            var submitData = await GithubHelper.Query(submitReviewMutation, _settings.GithubToken);
            return submitData["submitPullRequestReview"]["clientMutationId"] != null;
        }

        private async Task Reject(string date, string qbLink)
        {
            Console.Write("Check QB file... ");
            var qbResult = GetQbResult();
            if (qbResult.Equals("Passed", StringComparison.OrdinalIgnoreCase))
            {
                throw new ReviewException("Ticket should be rejected, but QB checks passed. Please check QB file");
            }
            else
            {
                Console.WriteLine("done");
            }

            Console.Write("Get failed rules... ");
            var failedItems = GetFailedItems();
            Console.WriteLine("done");

            if (_settings.RejectPr)
            {
                Console.Write("Reject PR... ");
                await ReviewPr(qbLink, false, failedItems);
                Console.WriteLine("done");
            }

            Console.Write("Reject ticket... ");
            _issue["QB Checklist Report"] = qbLink;
            await _issue.WorkflowTransitionAsync("Review rejected");
            Console.WriteLine("done");

            AddRecordToHistorySheet(date, failedItems);
        }

        private void AddRecordToHistorySheet(string date, IReadOnlyCollection<string> failedItems)
        {
            Console.Write("Add record to the history sheet... ");

            // Get rows count
            var rowCountAddress = $"{_settings.HistorySystemSheet}!B1:B1";
            var rowGetQuery = _sheetsService.Spreadsheets.Values.Get(_settings.HistoryDocumentId, rowCountAddress);
            var rowGetResult = rowGetQuery.Execute();
            var row = int.Parse(rowGetResult.Values[0][0].ToString());

            // Increment row count and save
            var rowSetQuery = _sheetsService.Spreadsheets.Values.Update(
                new ValueRange
                {
                    Values = new List<IList<object>>
                    {
                        new List<object> { (row + 1).ToString() },
                    }
                },
                _settings.HistoryDocumentId,
                rowCountAddress);
            rowSetQuery.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.RAW;
            rowSetQuery.Execute();

            // Add new row
            var rangeLetter = (char)('F' + failedItems.Count - 1);
            var insertNewRecordQuery = _sheetsService.Spreadsheets.Values.Update(
                new ValueRange
                {
                    Values = new List<IList<object>>
                    {
                        new List<object>
                            {
                                $"{date}",
                                _issue.Key.ToString(),
                                _prAuthor.DisplayName,
                                _jiraUser.DisplayName,
                                _issue.Type.ToString()
                            }
                            .Concat(failedItems)
                            .ToList(),
                    }
                },
                _settings.HistoryDocumentId,
                $"{_settings.HistorySheetName}!A{row}:{rangeLetter}{row}");
            insertNewRecordQuery.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.RAW;
            insertNewRecordQuery.Execute();

            Console.WriteLine("done");
        }

        private IReadOnlyCollection<string> GetFailedItems()
        {
            var failedItems = new List<string>();
            foreach (var page in _settings.SheetPages)
            {
                var sheetGetItemsQuery = _sheetsService.Spreadsheets.Values.Get(
                    _qbFile.Id,
                    $"{page.Name}!A3:H{(page.RulesCount + 2)}");
                var sheetGetItemsResult = sheetGetItemsQuery.Execute();

                foreach (var value in sheetGetItemsResult.Values)
                {
                    var ruleResult = value[0].ToString();
                    if (!ruleResult.Equals("fail", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var ruleNumber = value[6].ToString();
                    var ruleText = value[7].ToString();

                    failedItems.Add($"{ruleNumber}. {ruleText}");
                }
            }

            return failedItems;
        }

        private string GetQbResult()
        {
            var query = _sheetsService.Spreadsheets.Values.Get(_qbFile.Id, "Summary!B15");
            var result = query.Execute();
            return result.Values[0][0].ToString();
        }
    }
}