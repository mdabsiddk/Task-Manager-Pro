using System;

namespace TaskManagerPro
{
    /// <summary>
    /// The app's version, from one place. The number lives only in the VERSION file at the repository
    /// root: build.bat stamps it into the program file, and the window, About, tray, updater and
    /// "Installed apps" all read it from there. To change the version, change VERSION.
    /// </summary>
    internal static class AppVersion
    {
        /// <summary>Major.Minor.Patch of this program file, e.g. 2.2.0.</summary>
        public static readonly Version Current = Read();

        /// <summary>"2.2.0"</summary>
        public static string Text
        {
            get { return Current.ToString(); }
        }

        private static Version Read()
        {
            Version v = typeof(AppVersion).Assembly.GetName().Version;
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }
}
