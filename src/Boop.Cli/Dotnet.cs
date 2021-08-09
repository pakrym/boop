using System;
using System.Diagnostics;

namespace Boop.Cli
{
    public class Dotnet
    {
        public static void Run(string arguments, string workingDirectory = null)
        {
            if (new ProcessStartInfo("dotnet", arguments)
            {
                WorkingDirectory = workingDirectory
            }.ExecuteAndCaptureOutput(out var stdOut, out var stdErr) != 0)
            {
                Console.Error.WriteLine("Failed to init user secrets." + stdOut + stdErr);
                Environment.Exit(0);
            }
        }
    }
}