using System.Diagnostics;
using Microsoft.DotNet.Cli.Utils;

namespace Boop.Cli
{
    internal static class ProcessStartInfoExtensions
    {
        public static int ExecuteAndCaptureOutput(this ProcessStartInfo startInfo, out string stdOut, out string stdErr)
        {
            var outStream = new StreamForwarder().Capture();
            var errStream = new StreamForwarder().Capture();

            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;

            var process = new Process
            {
                StartInfo = startInfo
            };

            process.EnableRaisingEvents = true;

            process.Start();

            var taskOut = outStream.BeginRead(process.StandardOutput);
            var taskErr = errStream.BeginRead(process.StandardError);

            process.WaitForExit();

            taskOut.Wait();
            taskErr.Wait();

            stdOut = outStream.CapturedOutput;
            stdErr = errStream.CapturedOutput;

            return process.ExitCode;
        }
    }
}