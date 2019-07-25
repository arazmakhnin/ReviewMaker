using System;

namespace ReviewMaker.Exceptions
{
    public class ReviewException : Exception
    {
        public ReviewException(string message) : base(message)
        {
            
        }
    }
}