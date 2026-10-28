using System;
using System.IO;

namespace Genesis.Core
{
    /// <summary>
    /// Пути к файлам, которые приложение пишет само. Они лежат в пользовательском
    /// каталоге настроек, а не рядом с исполняемым файлом: релизная сборка может
    /// стоять в любом месте, и мусорить в её папке не нужно.
    /// </summary>
    public static class AppPaths
    {
        /// <summary>
        /// Каталог настроек: <c>%APPDATA%\Genesis</c> на Windows и
        /// <c>~/.config/genesis</c> на Linux (<c>XDG_CONFIG_HOME</c>, если задан).
        /// </summary>
        public static string ConfigDirectory { get; } = ResolveConfigDirectory();

        private static string ResolveConfigDirectory()
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(root))
                root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            string directory = Path.Combine(root, OperatingSystem.IsWindows() ? "Genesis" : "genesis");

            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Каталог настроек недоступен (экзотическая система, права): не роняем
                // приложение из-за раскладки окон, а уходим во временный каталог.
                Console.WriteLine($"[Genesis] Каталог настроек недоступен ({ex.Message}), использую {Path.GetTempPath()}");
                directory = Path.Combine(Path.GetTempPath(), "genesis");
                Directory.CreateDirectory(directory);
            }

            return directory;
        }
    }
}
