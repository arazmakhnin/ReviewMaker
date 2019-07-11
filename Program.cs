using System;
using System.Threading.Tasks;

namespace ReviewMaker
{
    class Program
    {
        static async Task Main(string[] args)
        {
            try
            {
                var reviewWorker = new ReviewWorker();
                await reviewWorker.DoWork();
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                Console.ReadKey();
            }
        }
    }
}
