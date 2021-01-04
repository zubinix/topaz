using OpenQA.Selenium;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using Topaz.Extra;

namespace Topaz.Web
{
    public class ScreenshotManager
    {
        private string testname = "";

        private readonly object FileOutputLock = new object();

        private string envron = null;

        private class Shots
        {
            public Screenshot sh;
            public String desc;
            public String testnm;
        };

        private List<Shots> imageSave = new List<Shots>();

        public void AddShot(Screenshot screensh, String description)
        {
            imageSave.Add(new Shots() { sh = screensh, desc = $"{description }", testnm = $"{testname}" }) ; 
        }

        public void SetTestName(string testnm)
        {
            if (testnm == null)
            {
                // clear name
                testname = "";
            }
            else
            {
                // set test name
                testname = testnm + '_';
            }           
        }

        public void WriteScreenshots()
        {
            int counter = 0;

            lock(FileOutputLock)
            {
                foreach (var image in imageSave)
                {
                    // replace non-word/digit characters from description so it can be used as filename
                    String filename = Regex.Replace(image.desc, @"[^\d\w-_]", "_");

                    // trim filename to no longer than 100 chars
                    if (filename.Length > 100)
                    {
                        filename = filename.Substring(0, 99);
                    }

                    // increment counter so files can be listed in order
                    counter++;

                    // get thread id
                    int thread_id = Thread.CurrentThread.ManagedThreadId;

                    // write screenshot to file
                    image.sh.SaveAsFile($"{Utils.GetSelectedEnvironment()}_{thread_id}_{image.testnm}_{CalculateCharCounter(counter)}_{counter}_{filename}.png", ScreenshotImageFormat.Png);
                }
            }           
        }

        private string CalculateCharCounter(int countvalue)
        {
            string charcounter = "";
            int basenumsys = 26;

            int b = countvalue;

            while(b >= basenumsys)
            {              
                int m = b % basenumsys;
                charcounter =  Convert.ToChar( 65 + m ) + charcounter;

                b /= basenumsys;
            }

            charcounter = Convert.ToChar( 65 + b ) + charcounter;
            
            while(charcounter.Length < 4)
            {
                charcounter = 'A' + charcounter;
            }

            return charcounter;
        }
    }
}
