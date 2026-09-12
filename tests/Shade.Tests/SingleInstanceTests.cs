using System.Diagnostics;

internal static class SingleInstanceTests
{
    public static void Run()
    {
        var name = "Shade.Tests.SingleInstance." + Guid.NewGuid().ToString("N");
        Process Start()
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true
            };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(SingleInstanceTests).Assembly.Location);
            start.ArgumentList.Add("--single-instance-probe");
            start.ArgumentList.Add(name);
            return Process.Start(start)!;
        }
        static void Expect(Process process, string message)
        {
            var line = process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            if (line != message) throw new Exception($"Expected {message}, received {line}");
        }
        static void Stop(Process process)
        {
            if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); }
            process.Dispose();
        }
        var owner = Start();
        try
        {
            Expect(owner, "acquired");
            var duplicate = Start();
            try
            {
                Expect(duplicate, "duplicate");
                if (!duplicate.WaitForExit(5000) || duplicate.ExitCode != 3)
                    throw new Exception("Duplicate did not exit promptly with code 3");
                if (owner.HasExited) throw new Exception("Duplicate interrupted the owner");
            }
            finally { Stop(duplicate); }
            owner.StandardInput.WriteLine(); owner.StandardInput.Flush();
            if (!owner.WaitForExit(5000) || owner.ExitCode != 0) throw new Exception("Owner did not exit cleanly");
        }
        finally { Stop(owner); }
        owner = Start();
        try { Expect(owner, "acquired"); owner.Kill(); owner.WaitForExit(5000); }
        finally { Stop(owner); }
        owner = Start();
        try { Expect(owner, "acquired"); }
        finally { Stop(owner); }
    }
}
