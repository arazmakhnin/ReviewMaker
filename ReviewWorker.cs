using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Atlassian.Jira;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using Google.Apis.Util.Store;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReviewMaker.Helpers;
using File = Google.Apis.Drive.v3.Data.File;

namespace ReviewMaker
{
    internal class ReviewWorker
    {
        private const string ApplicationName = "ReviewMakerAurea";

        private Dictionary<string, string> _folders;

        public async Task DoWork()
        {
            Console.WriteLine("Start");

            var credential = GetGoogleUserCredential();

            var driveService = new DriveService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = ApplicationName,
            });

            var sheetsService = new SheetsService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = ApplicationName,
            });

            var regex = new Regex(@"CC-\d+$");

            var json = System.IO.File.ReadAllText(AppFolderHelper.GetFile("settings.json"));
            var jSettings = (JObject)JsonConvert.DeserializeObject(json);

            var jiraUser = jSettings["jiraUser"].Value<string>();
            var jiraPassword = jSettings["jiraPassword"].Value<string>();
            var githubToken = jSettings["githubToken"].Value<string>();
            var copyQbUrlIfPrApproved = jSettings["copyQbUrlIfPrApproved"].Value<bool>();

            if (string.IsNullOrWhiteSpace(githubToken))
            {
                ColorWriteLine("Github token is empty, so PR approving will be disabled", ConsoleColor.Yellow);
            }

            var jFolders = (JObject)jSettings["gdrive-folders"];
            _folders = new Dictionary<string, string>();
            foreach (var (key, value) in jFolders)
            {
                _folders.Add(key, value.Value<string>());
            }

            var serializer = new JsonSerializer();
            var pages = serializer.Deserialize<SheetPage[]>(jSettings["sheetPages"].CreateReader());
            
            Console.Write("Getting Jira user... ");
            var jira = Jira.CreateRestClient("https://jira.devfactory.com", jiraUser, jiraPassword);
            var jiraUserFull = await jira.Users.GetUserAsync(jiraUser);
            Console.WriteLine("done");
            
            while (true)
            {
                Console.WriteLine("=========================================");

                Console.Write("Enter command [qbonly, reject] or Jira ticket: ");
                var ticketUrl = Console.ReadLine() ?? string.Empty;

                var command = Command.Approve;
                var parts = ticketUrl.Split(' ');
                if (parts[0].Trim() == "qbonly")
                {
                    command = Command.QbOnly;

                    if (parts.Length == 1)
                    {
                        Console.Write("Ok, qb only. Jira ticket: ");
                        ticketUrl = Console.ReadLine() ?? string.Empty;
                    }
                    else
                    {
                        Console.Write("Ok, qb only.");
                        ticketUrl = parts[1].Trim();
                    }
                }
                else if (parts[0].Trim() == "reject")
                {
                    command = Command.Reject;
                    if (parts.Length == 1)
                    {
                        Console.Write("Ok, will reject this ticket. Jira ticket: ");
                        ticketUrl = Console.ReadLine() ?? string.Empty;
                    }
                    else
                    {
                        Console.WriteLine("Ok, will reject this ticket.");
                        ticketUrl = parts[1].Trim();
                    }
                }

                TaskbarProgress.SetValue(0, 6);
                TaskbarProgress.SetState(TaskbarState.NoProgress);

                var m = regex.Match(ticketUrl);
                if (!ticketUrl.ToLower().StartsWith("https://") || !m.Success)
                {
                    TaskbarProgress.SetValue(1, 6);
                    TaskbarProgress.SetState(TaskbarState.Error);
                    Console.WriteLine("Unknown jira ticket");
                    continue;
                }

                Console.Write("Get ticket... ");
                var issue = await jira.Issues.GetIssueAsync(m.Value);
                var prAuthor = await jira.Users.GetUserAsync(issue.Assignee);
                Console.WriteLine("done");

                TaskbarProgress.SetValue(1, 6);

                if (issue.Status.ToString() != "Ready For Review" && issue.Status.ToString() != "Code Review")
                {
                    TaskbarProgress.SetState(TaskbarState.Error);
                    Console.WriteLine($"Unknown ticket state: {issue.Status}");
                    continue;
                }

                Console.Write("Set peer reviewer... ");
                if (issue.CustomFields["Peer reviewer"] == null)
                {
                    issue.CustomFields.Add("Peer reviewer", jiraUser);
                    await jira.Issues.UpdateIssueAsync(issue);

                    // Need to reload ticket. Otherwise we can't move it forward to the next status
                    issue = await jira.Issues.GetIssueAsync(m.Value);
                    Console.WriteLine("done");
                }
                else
                {
                    var reviewer = issue.CustomFields["Peer reviewer"].Values.First();
                    Console.WriteLine("already set to " + reviewer);
                    if (reviewer != jiraUser)
                    {
                        TaskbarProgress.SetState(TaskbarState.Paused);
                    }
                }

                TaskbarProgress.SetValue(2, 6);

                var prUrl = issue["Code Review Ticket URL"].ToString();
                if (!GithubHelper.ParsePrUrl(prUrl, out var repoName, out var prNumber))
                {
                    Console.WriteLine($"Invalid PR url: {prUrl}");
                    TaskbarProgress.SetState(TaskbarState.Error);
                    continue;
                }

                var qbFolder = repoName;
                var qbFileName = $"{repoName}/pull/{prNumber}";
                if (!_folders.ContainsKey(qbFolder))
                {
                    Console.WriteLine($"Unknown QB folder: {qbFolder}");
                    TaskbarProgress.SetState(TaskbarState.Error);
                    continue;
                }

                Console.Write("Create QB file... ");
                var file = CreateQbFile(qbFolder, qbFileName, driveService);
                Console.WriteLine("done");

                TaskbarProgress.SetValue(3, 6);

                Console.Write("Fill QB file... ");
                FillQbSummary(sheetsService, file, ticketUrl, prUrl, prAuthor.DisplayName, jiraUserFull.DisplayName);
                FillQbSheets(sheetsService, file, pages, issue.Type.ToString());
                Console.WriteLine("done");

                Console.Write("Check QB file... ");
                var qbResult = GetQbResult(sheetsService, file);
                if (qbResult.Equals("Passed", StringComparison.OrdinalIgnoreCase))
                {
                    ColorWriteLine("passed", ConsoleColor.Green);
                }
                else
                {
                    ColorWriteLine(qbResult, ConsoleColor.Red);
                }

                TaskbarProgress.SetValue(4, 6);

                var qbLink = $"https://docs.google.com/spreadsheets/d/{file.Id}/edit#gid=1247094356";

                var prApproved = false;
                if (command != Command.QbOnly)
                {
                    var message = command == Command.Approve ? "Move ticket to \"Code merge\"... " : "Reject ticket... ";
                    Console.Write(message);

                    if (issue.Status.ToString() != "Code Review")
                    {
                        await issue.WorkflowTransitionAsync("Start Review");
                    }

                    TaskbarProgress.SetValue(5, 6);

                    issue["QB Checklist Report"] = qbLink;

                    if (command == Command.Approve)
                    {
                        await issue.WorkflowTransitionAsync("Code review approved");
                    }
                    else if (command == Command.Reject)
                    {
                        await issue.WorkflowTransitionAsync("Review rejected");
                    }

                    TaskbarProgress.SetValue(6, 6);

                    Console.WriteLine("done");

                    if (command == Command.Approve && !string.IsNullOrWhiteSpace(githubToken))
                    {
                        Console.Write("Approve PR... ");
                        if (await ApprovePr(githubToken, repoName, prNumber, qbLink))
                        {
                            Console.WriteLine("done");
                            prApproved = true;
                        }
                        else
                        {
                            ColorWriteLine("error", ConsoleColor.Red);
                        }
                    }
                }
                else
                {
                    TaskbarProgress.SetValue(6, 6);
                }
                
                Console.WriteLine("QB url: " + qbLink);

                if (copyQbUrlIfPrApproved || !prApproved)
                {
                    WindowsClipboard.SetText(qbLink);
                    Console.WriteLine("Url copied to clipboard");
                }

                //TaskbarProgress.SetState(TaskbarState.NoProgress);
            }
        }

        private static void ColorWriteLine(string text, ConsoleColor color)
        {
            var c = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ForegroundColor = c;
        }

        private async Task<bool> ApprovePr(string githubToken, string repoName, string prNumber, string qbLink)
        {
            var query = @"query {
                repository(owner:""trilogy-group"", name:""" + repoName + @"""){
                    pullRequest(number: " + prNumber + @"){
                        id
                        body
                    }
                }
            }";

            var repoData = await GithubHelper.Query(query, githubToken);
            var prId = repoData["repository"]["pullRequest"]["id"].Value<string>();
            var body = repoData["repository"]["pullRequest"]["body"].Value<string>();

            var qbInBody = body.Contains("- Please insert QB sheet here");


            var addReviewMutation = @"mutation w{ 
                addPullRequestReview(input: {pullRequestId: """ + prId + @"""}) { 
                    pullRequestReview {id}
                }
            }";

            var reviewData = await GithubHelper.Query(addReviewMutation, githubToken);
            var reviewId = reviewData["addPullRequestReview"]["pullRequestReview"]["id"].Value<string>();


            var reviewBody = string.Empty;
            if (!qbInBody)
            {
                reviewBody = "body: \"QB: " + qbLink + "\"";
            }

            var submitReviewMutation = @"mutation e { 
                submitPullRequestReview(input: {
                    " + reviewBody + @"
                    pullRequestReviewId: """ + reviewId + @""",
                    event: APPROVE}) { 
                        clientMutationId
                }
            }";

            var submitData = await GithubHelper.Query(submitReviewMutation, githubToken);
            var isOk = submitData["submitPullRequestReview"]["clientMutationId"] != null;
            if (isOk && qbInBody)
            {
                var newPrBody = body
                    .Replace("- Please insert QB sheet here", "- QB: " + qbLink);
                    //.Replace("\r", "\\r")
                    //.Replace("\n", "\\n");

                var editPrBodyMutation = @"mutation r {
                    updatePullRequest(input: {
                        pullRequestId: """ + prId + @""",
                        body: """ + newPrBody + @"""}){ 
                            pullRequest {
                                body
                            }
                    }
                }";

                var editPrBodyData = await GithubHelper.Query(editPrBodyMutation, githubToken);
                isOk = editPrBodyData["updatePullRequest"]["pullRequest"]["body"] != null;
            }

            return isOk;
        }

        private string GetQbResult(SheetsService sheetsService, File file)
        {
            var query = sheetsService.Spreadsheets.Values.Get(file.Id, "Summary!B15");
            var result = query.Execute();
            return result.Values[0][0].ToString();
        }

        private void FillQbSummary(SheetsService sheetsService, File file, string issueUrl, string prUrl, string prAuthor, string prReviewer)
        {
            var summary1 = sheetsService.Spreadsheets.Values.Update(
                new ValueRange
                {
                    Values = new List<IList<object>>
                    {
                        new List<object> { issueUrl },
                        new List<object> { prUrl },
                        new List<object> { prReviewer }
                    }
                },
                file.Id,
                "Summary!B7:B9");

            summary1.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.RAW;
            summary1.Execute();

            var summary2 = sheetsService.Spreadsheets.Values.Update(
                new ValueRange
                {
                    Values = new List<IList<object>>
                    {
                        new List<object> { prAuthor }
                    }
                },
                file.Id,
                "Summary!B11:B11");

            summary2.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.RAW;
            summary2.Execute();
        }

        private static void FillQbSheets(SheetsService sheetsService, File file, SheetPage[] pages, string issueType)
        {
            foreach (var page in pages.Where(p => p.RulesCount > 0))
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
                var pass1 = sheetsService.Spreadsheets.Values.Update(
                    data,
                    file.Id,
                    $"{page.Name}!A3:A{(page.RulesCount + 2)}");

                pass1.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.RAW;
                pass1.Execute();
            }
        }

        private File CreateQbFile(string qbFolder, string qbFileName, DriveService driveService)
        {
            var copy = new File
            {
                Parents = new List<string> { _folders[qbFolder] },
                CopyRequiresWriterPermission = true,
                Name = qbFileName,
            };

            const string templateId = "1zxyseum3dOcfZsk0QLdWsX3Y2F6c8yi-cFHVMTGSV1I";
            var copyRequest = driveService.Files.Copy(copy, templateId);
            var file = copyRequest.Execute();
            return file;
        }

        private static UserCredential GetGoogleUserCredential()
        {
            // If modifying these scopes, delete your previously saved credentials
            // at ~/.credentials/sheets.googleapis.com-dotnet-quickstart.json
            var scopes = new[]
            {
                SheetsService.Scope.Spreadsheets, 
                DriveService.Scope.Drive
            };

            UserCredential credential;

            var a = AppFolderHelper.GetFile("credentials.json");
            Console.WriteLine(a);
            using (var stream = new FileStream(a, FileMode.Open, FileAccess.Read))
            {
                // The file token.json stores the user's access and refresh tokens, and is created
                // automatically when the authorization flow completes for the first time.
                string credPath = "token";
                credential = GoogleWebAuthorizationBroker.AuthorizeAsync(
                    GoogleClientSecrets.Load(stream).Secrets,
                    scopes,
                    "user",
                    CancellationToken.None,
                    new FileDataStore(credPath, true)).Result;
                //Console.WriteLine("Credential file saved to: " + credPath);
            }

            return credential;
        }        
    }

    internal enum Command
    {
        Approve,
        QbOnly,
        Reject
    }

    public class SheetPage
    {
        public string Name { get; set; }
        public int RulesCount { get; set; }
        public string TicketTypeCondition { get; set; }
    }
    
    public static class AppFolderHelper
    {
        public static string GetAppFolder()
        {
            var codebase = Assembly.GetExecutingAssembly().CodeBase;
            var uri = new UriBuilder(codebase);
            var path = Uri.UnescapeDataString(uri.Path);
            return Path.GetDirectoryName(path);
        }

        public static string GetFile(string filename)
        {
            return Path.Combine(GetAppFolder(), filename);
        }
    }
}