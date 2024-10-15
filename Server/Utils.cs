using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Server
{
    public class Utils
    {
        public static string GetProjectDirectory(string targetFolder = "assets")
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string projectDirectory = baseDirectory;

            string[] solutionIndicators = { "HttpProxy.sln" };

            while (!solutionIndicators.Any(indicator => File.Exists(Path.Combine(projectDirectory, indicator)) || Directory.Exists(Path.Combine(projectDirectory, indicator))))
            {
                projectDirectory = Directory.GetParent(projectDirectory)?.FullName;
                if (projectDirectory == null)
                {
                    throw new DirectoryNotFoundException("Could not find the solution root directory.");
                }
            }

            string targetPath = Directory.GetDirectories(projectDirectory, targetFolder, SearchOption.AllDirectories).FirstOrDefault(dir => dir.EndsWith(targetFolder));

            if (targetPath == null)
            {
                throw new DirectoryNotFoundException($"Could not find the '{targetFolder}' directory.");
            }

            return targetPath;
        }
    }
}
