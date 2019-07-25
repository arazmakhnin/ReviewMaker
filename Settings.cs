using System.Collections.Generic;

namespace ReviewMaker
{
    public class Settings
    {
        public string JiraServer { get; set; }
        public string JiraUser { get; set; }
        public string JiraPassword { get; set; }
        public string GithubToken { get; set; }
        public string ApprovePrAction { get; set; }
        public bool RejectPr { get; set; }
        public string HistoryDocumentId { get; set; }
        public string HistorySheetName { get; set; }
        public string HistorySystemSheet { get; set; }
        public bool UseLocalDate { get; set; }
        public Dictionary<string, string> GoogleDriveFolders { get; set; }
        public SheetPage[] SheetPages { get; set; }
    }

    public class SheetPage
    {
        public string Name { get; set; }
        public int RulesCount { get; set; }
        public string TicketTypeCondition { get; set; }
    }
}