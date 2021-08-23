using System;
using System.Diagnostics;

namespace Boop.Cli
{
    public class Exec
    {
        public static string Run(string process, string arguments, string workingDirectory = null)
        {
            if (new ProcessStartInfo(process, arguments)
            {
                WorkingDirectory = workingDirectory
            }.ExecuteAndCaptureOutput(out var stdOut, out var stdErr) != 0)
            {
                Console.Error.WriteLine($"Failed to run {process} {arguments}. {stdOut}{stdErr}");
                Environment.Exit(0);
            }

            return stdOut;
        }
    }
}