using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;

namespace Boop.Cli
{
    public class AzCli
    {
        public static T Run<T>(string arguments)
        {
            if (TryRun(arguments, out var stdOut, out var stdErr))
            {
                Console.Error.WriteLine(stdErr);
                Environment.Exit(0);
            }

            return JsonSerializer.Deserialize<T>(stdOut);
        }

        public static T TryRun<T>(string arguments)
        {
            if (TryRun(arguments, out var stdOut, out var stdErr))
            {
                Console.Error.WriteLine(stdErr);
            }

            return JsonSerializer.Deserialize<T>(stdOut);
        }

        private static bool TryRun(string arguments, out string stdOut, out string stdErr)
        {
            var commandLine = $"/c az {arguments} -o json";
            return new ProcessStartInfo("cmd",  commandLine).ExecuteAndCaptureOutput(out stdOut, out stdErr) != 0;
        }

        public static AccountInfo CheckLogin()
        {
            var loginInfo = Run<AccountInfo>("account show");
            if (loginInfo == null)
            {
                Environment.Exit(1);
            }

            return loginInfo;
        }


        public record AccountInfo(string name, string id, AccountInfoUser user);

        public record AccountInfoUser(string name)
        {
            public string ShortName => name.Split("@").First();
        }
    }
}