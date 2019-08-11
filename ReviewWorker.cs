using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Atlassian.Jira;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Util.Store;
using Newtonsoft.Json;
using ReviewMaker.Helpers;

namespace ReviewMaker
{
    internal class ReviewWorker
    {
        private const string ApplicationName = "ReviewMakerAurea";

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

            var json = File.ReadAllText(AppFolderHelper.GetFile("settings.json"));
            var settings = JsonConvert.DeserializeObject<Settings>(json);

            if (string.IsNullOrWhiteSpace(settings.JiraServer))
            {
                Console.WriteLine("Invalid jira server");
                return;
            }

            if (string.IsNullOrWhiteSpace(settings.JiraUser) || string.IsNullOrWhiteSpace(settings.JiraPassword))
            {
                Console.WriteLine("Invalid jira credentials");
                return;
            }

            if (string.IsNullOrWhiteSpace(settings.GithubToken))
            {
                Console.WriteLine("Invalid github token");
                return;
            }

            Console.Write("Getting Jira user... ");
            var jira = Jira.CreateRestClient(settings.JiraServer, settings.JiraUser, settings.JiraPassword);
            var jiraUserFull = await jira.Users.GetUserAsync(settings.JiraUser);
            Console.WriteLine("done");

            var googleDriveFolders = new Dictionary<string, string>();
            
            while (true)
            {
                Console.WriteLine("=========================================");

                try
                {
                    var worker = new Worker(jira, jiraUserFull, driveService, sheetsService, settings, googleDriveFolders);
                    await worker.DoWork();
                }
                catch (Exception ex)
                {
                    ConsoleHelper.WriteLineColor(ex.ToString(), ConsoleColor.Red);
                }
            }
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
            //Console.WriteLine(a);
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
}