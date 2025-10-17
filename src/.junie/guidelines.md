Project: Bergdahl.NodePad.WebApp (ASP.NET Core, .NET 8)

This document captures project-specific build, configuration, testing, and development guidelines for advanced contributors.

1) Build and run
- Prerequisites: .NET SDK 8.x installed.
- Project location: src/Bergdahl.NodePad.WebApp
- Build: dotnet build src/Bergdahl.NodePad.WebApp
- Run (Development): dotnet run --project src/Bergdahl.NodePad.WebApp
  - The app uses ASP.NET Core minimal hosting. In Development, UseDeveloperExceptionPage is enabled; in other environments, /error handler is configured (route must be added if you introduce it).
  - Static files and default files are served from wwwroot. The main UI is wwwroot/index.html with assets under wwwroot/js and wwwroot/css.

Configuration notes
- appsettings.json keys of interest:
  - Logging: Standard ASP.NET Core logging with Console and Debug providers; log levels can be tuned via Logging.LogLevel.
  - AllowedHosts: Standard ASP.NET key.
  - PagesDirectory: The root folder for markdown content (default "Pages" under the current working directory). The API ensures the directory exists at startup and will create it if missing.
- Overriding config:
  - Put values in appsettings.Development.json or environment variables (e.g., PagesDirectory=/absolute/path/to/your/pages).
- Filesystem behavior and constraints (PagesController):
  - Only .md files are allowed for read/write operations.
  - Paths are sanitized to avoid traversal; inputs containing .. or backslashes are normalized. If the computed path does not reside under PagesDirectory, the operation is rejected.
  - GET /api/pages/structure enumerates directories and files (only *.md files) recursively and returns a tree of FileSystemEntity.
  - GET /api/pages/content?path=relative/path.md returns the file contents.
  - POST /api/pages/save?path=relative/path.md with a text/plain body saves/creates the file. The TextPlainInputFormatter is registered to allow string bodies without JSON wrappers.
  - POST /api/pages/create?path=relative/path[.md]&type=file|directory creates files (touch) or directories; attempts to create an existing entity return 400.
  - DELETE /api/pages/delete?path=relative/path deletes either a file (exact path) or a directory (recursive delete) within the PagesDirectory.

2) Testing
This repository does not ship with a test project by default. Below is the recommended approach for adding and running tests, plus a verified example.

Test project recommendation
- Create a parallel test project targeting net8.0 and reference the web project:
  - Directory: src/Bergdahl.NodePad.WebApp.Tests
  - Example csproj (xUnit):
    <Project Sdk="Microsoft.NET.Sdk">
      <PropertyGroup>
        <TargetFramework>net8.0</TargetFramework>
        <IsPackable>false</IsPackable>
        <Nullable>enable</Nullable>
      </PropertyGroup>
      <ItemGroup>
        <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
        <PackageReference Include="xunit" Version="2.9.0" />
        <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
        <PackageReference Include="coverlet.collector" Version="6.0.2" />
      </ItemGroup>
      <ItemGroup>
        <ProjectReference Include="../Bergdahl.NodePad.WebApp/Bergdahl.NodePad.WebApp.csproj" />
      </ItemGroup>
    </Project>

Guidelines for writing tests
- Prefer testing controller logic without spinning up Kestrel when possible. Instantiate controllers with:
  - IConfiguration built via ConfigurationBuilder.AddInMemoryCollection to point PagesDirectory to a temporary location.
  - ILogger via LoggerFactory.Create or Microsoft.Extensions.Logging.Abstractions if you need a NullLogger.
- Use temp directories under Path.GetTempPath() and clean them up in test teardown/finally blocks to avoid littering the filesystem.
- Exercise both valid and invalid paths to assert path sanitization and extension enforcement. Validate response types (OkResult, OkObjectResult, BadRequestObjectResult, NotFoundResult, etc.).

Worked example (verified)
- Example test code used to verify the flow locally:

  using System;
  using System.Collections.Generic;
  using System.IO;
  using Bergdahl.NodePad.WebApp;
  using Microsoft.AspNetCore.Mvc;
  using Microsoft.Extensions.Configuration;
  using Microsoft.Extensions.Logging;
  using Xunit;

  public class PagesControllerTests
  {
      private static (PagesController controller, string root) CreateControllerWithTempRoot()
      {
          var tempRoot = Path.Combine(Path.GetTempPath(), "NodePad_Test_" + Guid.NewGuid().ToString("N"));
          Directory.CreateDirectory(tempRoot);

          var inMemory = new Dictionary<string, string?> { { "PagesDirectory", tempRoot } };
          var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();
          using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
          var logger = loggerFactory.CreateLogger<PagesController>();

          return (new PagesController(config, logger), tempRoot);
      }

      [Fact]
      public void Save_And_Get_Page_Content_Roundtrip_Works()
      {
          var (controller, root) = CreateControllerWithTempRoot();
          try
          {
              var relPath = "folder1/test.md";
              var content = "Hello from test at " + DateTime.UtcNow.ToString("O");
              Assert.IsType<OkResult>(controller.SavePageContent(relPath, content));
              var ok = Assert.IsType<OkObjectResult>(controller.GetPageContent(relPath));
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
          var (controller, root) = CreateControllerWithTempRoot();
          try
          {
              Assert.IsType<BadRequestObjectResult>(controller.SavePageContent(badPath, "irrelevant"));
          }
          finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
      }
  }

- Command used to run during verification: dotnet test src/Bergdahl.NodePad.WebApp.Tests
- Observed output (abridged):
  Restored ...
  Bergdahl.NodePad.WebApp -> .../bin/Debug/net8.0/Bergdahl.NodePad.WebApp.dll
  Bergdahl.NodePad.WebApp.Tests -> .../bin/Debug/net8.0/Bergdahl.NodePad.WebApp.Tests.dll
  Starting test execution, please wait...
  info: Bergdahl.NodePad.WebApp.PagesController[0]
        File saved: folder1/test.md
  Passed!  - Failed: 0, Passed: 5, Skipped: 0, Total: 5, Duration: 8 ms - Bergdahl.NodePad.WebApp.Tests.dll (net8.0)

Notes on testing the HTTP layer
- If you need end-to-end HTTP tests, use WebApplicationFactory<TEntryPoint> from Microsoft.AspNetCore.Mvc.Testing. This project currently uses a top-level Program.cs with minimal hosting; you can still create a factory by adding a partial Program class for test discovery or by targeting the assembly directly. Keep these tests in a separate test project and avoid network ports by using the TestServer.

3) Additional development information
Code style and language
- C# 12 / .NET 8 features are available. Nullable is enabled in the sample test setup; the web project itself uses SDK defaults. Prefer explicit nullability annotations.
- Keep controllers slim and focused on IO and validation; push reusable filesystem logic behind private helpers (as currently done with GetSafePath).

Security and IO
- Path handling: Always route any user-provided paths through the same sanitization rules as PagesController (remove .. and backslashes, normalize via Path.GetFullPath, verify prefix under PagesDirectory, and enforce .md extension). This prevents directory traversal and unintended file types.
- Only text/plain bodies are accepted for SavePageContent because of TextPlainInputFormatter; if you add new endpoints that accept raw text, reuse or extend this formatter. For JSON inputs, use standard [FromBody] model binding without this formatter.
- Deletions: Directory deletes are recursive. Be mindful when adapting behavior; keep the root check intact.

Logging and diagnostics
- Logging providers: Console and Debug are configured. Use structured logging (_logger.LogInformation(..., "{Path}", path)) as in the code.
- In development, the Developer Exception Page is active; add a centralized /error endpoint implementation if you rely on it in non-development.

Front-end
- Static assets live under wwwroot. Any changes to index.html, js, or css are served directly by StaticFiles middleware. There is no server-side view engine.

Extensibility ideas
- If you introduce markdown parsing or rendering, keep PagesDirectory as source-of-truth and avoid serving raw untrusted HTML without sanitization.
- Consider adding integration tests around the API endpoints with TestServer to validate serialization and error codes.

Maintenance notes
- FileSystemEntity currently returns combined files and directories (directories recursively). If you need ordering or filtering, implement it within GetDirectoryStructure.
- The API returns IActionResult; when designing clients, account for 400/403/404/500 cases in addition to 200.

How to add your own tests (summary)
- Create a new test project under src, reference the web project, add xUnit tests following the patterns above, and run dotnet test from the repository root or project directory. Keep filesystem writes scoped to temp folders and ensure teardown cleans up.
