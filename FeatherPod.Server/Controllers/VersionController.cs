using System.Reflection;
using FeatherPod.Server.Models;
using Microsoft.AspNetCore.Mvc;

namespace FeatherPod.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class VersionController : ControllerBase
{
    private readonly IHostEnvironment _environment;

    public VersionController(IHostEnvironment environment)
    {
        _environment = environment;
    }

    [HttpGet]
    [ProducesResponseType<VersionInfo>(StatusCodes.Status200OK)]
    public IActionResult GetVersion()
    {
        var assembly = Assembly.GetEntryAssembly();
        var versionAttribute = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        var version = versionAttribute?.InformationalVersion ?? "unknown";

        // Get build date from assembly file modification time
        var assemblyLocation = assembly?.Location;
        var buildDate = assemblyLocation != null && System.IO.File.Exists(assemblyLocation)
            ? System.IO.File.GetLastWriteTimeUtc(assemblyLocation).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
            : DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

        var versionInfo = new VersionInfo
        {
            Version = version,
            BuildDate = buildDate,
            Environment = _environment.EnvironmentName,
            TargetFramework = assembly?.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()?.FrameworkName
        };

        return Ok(versionInfo);
    }
}
