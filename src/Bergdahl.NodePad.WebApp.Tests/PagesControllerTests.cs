using System;
using System.Collections.Generic;
using System.IO;
using Bergdahl.NodePad.WebApp;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Bergdahl.NodePad.WebApp.Tests
{
    public class PagesControllerTests
    {
        private static (PagesController controller, string root, ILogger<PagesController> logger) CreateControllerWithTempRoot()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "NodePad_Test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);

            var inMemory = new Dictionary<string, string?> { { "PagesDirectory", tempRoot } };
            var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();
            using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
            var logger = loggerFactory.CreateLogger<PagesController>();

            return (new PagesController(config, logger), tempRoot, logger);
        }

        [Fact]
        public void Save_And_Get_Page_Content_Roundtrip_Works()
        {
            var (controller, root, _) = CreateControllerWithTempRoot();
            try
            {
                var relPath = "folder1/test.md";
                var content = "Hello from test at " + DateTime.UtcNow.ToString("O");
                var save = Assert.IsType<ContentResult>(controller.SavePageContent(relPath, content, null));
                                Assert.Equal(content, save.Content);
                                Assert.Equal("text/plain", save.ContentType);
                var ok = Assert.IsType<OkObjectResult>(controller.GetPageContent(relPath, includeMeta: false));
                Assert.Equal(content, Assert.IsType<string>(ok.Value));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [Theory]
        [InlineData("../hack.md")]
        [InlineData("..//hack.md")]
        [InlineData("hack.txt")]
        public void Invalid_Paths_Are_Rejected_By_Save(string badPath)
        {
            var (controller, root, _) = CreateControllerWithTempRoot();
            try
            {
                Assert.IsType<BadRequestObjectResult>(controller.SavePageContent(badPath, "irrelevant", null));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [Fact]
        public void Create_And_Delete_Directory_Works()
        {
            var (controller, root, _) = CreateControllerWithTempRoot();
            try
            {
                var relDir = "newFolder";
                var create = Assert.IsType<OkResult>(controller.CreateEntity(relDir, "directory"));
                Assert.True(Directory.Exists(Path.Combine(root, relDir)));

                var del = Assert.IsType<OkResult>(controller.DeleteEntity(relDir, recursive: true));
                Assert.False(Directory.Exists(Path.Combine(root, relDir)));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [Fact]
        public void Create_File_Then_Rename_And_Move_Works()
        {
            var (controller, root, _) = CreateControllerWithTempRoot();
            try
            {
                // Create initial folder and file
                Assert.IsType<OkResult>(controller.CreateEntity("docs", "directory"));
                Assert.IsType<OkResult>(controller.CreateEntity("docs/readme.md", "file"));
                Assert.True(File.Exists(Path.Combine(root, "docs", "readme.md")));

                // Rename
                var rename = controller.Rename("docs/readme.md", "intro.md");
                Assert.IsType<OkObjectResult>(rename);
                Assert.True(File.Exists(Path.Combine(root, "docs", "intro.md")));
                Assert.False(File.Exists(Path.Combine(root, "docs", "readme.md")));

                // Move to another folder
                Assert.IsType<OkResult>(controller.CreateEntity("archive", "directory"));
                var move = controller.Move("docs/intro.md", "archive");
                Assert.IsType<OkObjectResult>(move);
                Assert.True(File.Exists(Path.Combine(root, "archive", "intro.md")));
                Assert.False(File.Exists(Path.Combine(root, "docs", "intro.md")));

                // Now move file to root (destination = empty string)
                var moveToRoot = controller.Move("archive/intro.md", "");
                Assert.IsType<OkObjectResult>(moveToRoot);
                Assert.True(File.Exists(Path.Combine(root, "intro.md")));
                Assert.False(File.Exists(Path.Combine(root, "archive", "intro.md")));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [Fact]
        public void ValidateName_Respects_Rules_And_Extension()
        {
            var (controller, root, _) = CreateControllerWithTempRoot();
            try
            {
                var validName = Assert.IsType<OkObjectResult>(controller.ValidateName(path: string.Empty, type: "file", name: "note"));
                                var validObj = validName.Value!;
                                var validProp1 = validObj.GetType().GetProperty("valid");
                                Assert.NotNull(validProp1);
                                var isValid1 = (bool)(validProp1!.GetValue(validObj) ?? false);
                                Assert.True(isValid1);
                var bad = controller.ValidateName(path: null, type: "file", name: "inv*alid");
                var badOk = Assert.IsType<OkObjectResult>(bad);
                var badObj = badOk.Value!;
                var validProp = badObj.GetType().GetProperty("valid");
                Assert.NotNull(validProp);
                var isValid = (bool)(validProp!.GetValue(badObj) ?? false);
                Assert.True(isValid);
                var suggestedNameProp = badObj.GetType().GetProperty("suggestedName");
                Assert.NotNull(suggestedNameProp);
                var suggested = Assert.IsType<string>(suggestedNameProp!.GetValue(badObj));
                Assert.NotEqual("inv*alid", suggested);
                Assert.NotEmpty(suggested);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [Fact]
        public void Tags_Are_Saved_And_Returned_In_GetPageMeta()
        {
            var (controller, root, _) = CreateControllerWithTempRoot();
            try
            {
                var relPath = "folder1/tags-sample.md";
                var saved = Assert.IsType<ContentResult>(controller.SavePageContent(relPath, "content", tags: "CSharp,  dotnet ,  ASP.NET"));
                                Assert.Equal("content", saved.Content);

                var meta = controller.GetPageMeta(relPath, includeNormalized: true);
                var ok = Assert.IsType<OkObjectResult>(meta);
                var metaObj = ok.Value!;
                // metaObj is an anonymous type with properties: display and normalized
                var displayProp = metaObj.GetType().GetProperty("display");
                var normalizedProp = metaObj.GetType().GetProperty("normalized");
                Assert.NotNull(displayProp);
                Assert.NotNull(normalizedProp);
                var normalized = Assert.IsType<string[]>(normalizedProp!.GetValue(metaObj));
                Assert.Contains("csharp", normalized);
                Assert.Contains("dotnet", normalized);
                Assert.Contains("asp.net", normalized);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}