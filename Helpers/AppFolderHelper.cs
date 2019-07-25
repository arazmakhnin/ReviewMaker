using System;
using System.IO;
using System.Reflection;

namespace ReviewMaker
{
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