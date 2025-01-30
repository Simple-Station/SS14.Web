using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace SS14.Auth.Controllers;

[ApiController]
[Route("/api")]
public sealed class ApiController(IConfiguration cfg) : ControllerBase
{
    private string WebBaseUrl => cfg.GetValue<string>("WebBaseUrl") ?? "";

    [HttpGet("accountSite")]
    public Task<IActionResult> GetAccountSite()
    {
        return Task.FromResult<IActionResult>(Ok(new {WebBaseUrl}));
    }
}
