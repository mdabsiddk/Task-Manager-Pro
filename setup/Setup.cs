using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace TaskManagerPro.Setup
{
    /// <summary>
    /// TaskManagerPro-Setup.exe: a single-file installer for GitHub Releases. The release package
    /// (TaskManagerPro.exe, its .config, install.bat, uninstall.bat) is embedded as resources named
    /// "pkg/&lt;file&gt;". Setup extracts them to a fresh temp folder, runs install.bat (which sees the
    /// pre-built exe and skips building) and deletes the temp folder again. The manifest asks for
    /// administrator rights, so Windows shows one UAC prompt.
    ///
    ///   TaskManagerPro-Setup.exe                 install / update (interactive console)
    ///   TaskManagerPro-Setup.exe --quiet         install without waiting for a key press
    ///   TaskManagerPro-Setup.exe --extract DIR   only extract the package to DIR
    /// </summary>
    internal static class Program
    {
        private const string Prefix = "pkg/";

        private static int Main(string[] args)
        {
            Console.Title = "Task Manager Pro - Setup";
            try
            {
                int extractAt = Array.FindIndex(args, a => string.Equals(a, "--extract", StringComparison.OrdinalIgnoreCase));
                if (extractAt >= 0)
                {
                    if (extractAt + 1 >= args.Length) throw new ArgumentException("--extract needs a folder");
                    Extract(args[extractAt + 1]);
                    Console.WriteLine("Extracted to " + Path.GetFullPath(args[extractAt + 1]));
                    return 0;
                }

                bool quiet = Array.Exists(args, a => string.Equals(a, "--quiet", StringComparison.OrdinalIgnoreCase));
                string dir = Path.Combine(Path.GetTempPath(), "TaskManagerPro-Setup-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Extract(dir);
                    var psi = new ProcessStartInfo("cmd.exe", "/c \"\"" + Path.Combine(dir, "install.bat") + "\"" + (quiet ? " < nul\"" : "\""))
                    {
                        UseShellExecute = false,
                        WorkingDirectory = dir
                    };
                    using (Process p = Process.Start(psi))
                    {
                        p.WaitForExit();
                        return p.ExitCode;
                    }
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[X] Setup failed: " + ex.Message);
                Console.ResetColor();
                Console.WriteLine("Press Enter to close.");
                Console.ReadLine();
                return 1;
            }
        }

        private static void Extract(string dir)
        {
            Directory.CreateDirectory(dir);
            Assembly asm = Assembly.GetExecutingAssembly();
            int count = 0;
            foreach (string name in asm.GetManifestResourceNames())
            {
                if (!name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
                string file = Path.GetFileName(name.Substring(Prefix.Length)); // no sub-paths
                using (Stream src = asm.GetManifestResourceStream(name))
                using (FileStream dst = File.Create(Path.Combine(dir, file)))
                {
                    src.CopyTo(dst);
                }
                count++;
            }
            if (count == 0 || !File.Exists(Path.Combine(dir, "install.bat")))
                throw new InvalidOperationException("This setup file is damaged (no package inside). Download it again.");
        }
    }
}
