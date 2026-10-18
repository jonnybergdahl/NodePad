using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace Bergdahl.NodePad.WebApp.Tests
{
    /// <summary>
    /// Guards the web project's file settings. IDEs can drop these items when files are deleted or
    /// re-added, which would put the server's settings or the account's password hash into publish output.
    /// </summary>
    public class ProjectFileTests
    {
        private static XDocument LoadWebProject()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Bergdahl.NodePad.WebApp", "Bergdahl.NodePad.WebApp.csproj");
                if (File.Exists(candidate)) return XDocument.Load(candidate);
                dir = dir.Parent;
            }
            throw new FileNotFoundException("Bergdahl.NodePad.WebApp.csproj not found above " + AppContext.BaseDirectory);
        }

        private static XElement Item(XDocument project, string file) =>
            project.Descendants()
                .SingleOrDefault(e => (string?)e.Attribute("Update") == file || (string?)e.Attribute("Include") == file)
            ?? throw new Xunit.Sdk.XunitException($"Bergdahl.NodePad.WebApp.csproj has no item for {file}");

        [Fact]
        public void Account_File_Is_Never_Copied_To_Output()
        {
            var item = Item(LoadWebProject(), "nodepad.auth.json");
            Assert.Equal("Never", (string?)item.Attribute("CopyToOutputDirectory"));
            Assert.Equal("Never", (string?)item.Attribute("CopyToPublishDirectory"));
        }

        [Fact]
        public void Settings_File_Is_Not_Published()
        {
            var item = Item(LoadWebProject(), "nodepad.json");
            Assert.Equal("Never", (string?)item.Attribute("CopyToPublishDirectory"));
        }
    }
}
