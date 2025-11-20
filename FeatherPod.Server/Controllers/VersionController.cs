using System.Reflection;
using FeatherPod.Shared.Models;
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

        var versionInfo = new VersionInfo
        {
            Version = version,
            Environment = _environment.EnvironmentName,
            TargetFramework = assembly?.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()?.FrameworkName
        };

        return Ok(versionInfo);
    }
}
