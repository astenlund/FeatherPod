using FeatherPod.Server.Models;
using FeatherPod.Server.Services;
using FeatherPod.Server.Validation;
using FeatherPod.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace FeatherPod.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    private readonly IUserService _userService;
    private readonly ILogger<UsersController> _logger;

    public UsersController(IUserService userService, ILogger<UsersController> logger)
    {
        _userService = userService;
        _logger = logger;
    }

    [HttpGet("me")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult GetCurrentUser()
    {
        var user = HttpContext.Items["User"] as User;
        if (user == null)
        {
            return Unauthorized(new { error = "Not authenticated" });
        }

        return Ok(new
        {
            user.Id,
            user.Name,
            user.Email,
            user.Role,
            user.OwnedFeeds,
            user.CreatedAt,
            user.LastActive
        });
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListUsers()
    {
        var users = await _userService.GetAllUsersAsync();

        // Don't expose API key hashes
        var safeUsers = users.Select(u => new
        {
            u.Id,
            u.Name,
            u.Email,
            u.Role,
            u.OwnedFeeds,
            u.CreatedAt,
            u.LastActive
        });

        return Ok(safeUsers);
    }

    [HttpGet("{userId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetUser(string userId)
    {
        var user = await _userService.GetUserByIdAsync(userId);
        if (user == null)
        {
            return NotFound(new { error = $"User '{userId}' not found" });
        }

        // Don't expose API key hash
        var safeUser = new
        {
            user.Id,
            user.Name,
            user.Email,
            user.Role,
            user.OwnedFeeds,
            user.CreatedAt,
            user.LastActive
        };

        return Ok(safeUser);
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Id))
        {
            return BadRequest(new { error = "User ID is required" });
        }

        if (!InputValidation.IsValidUserId(request.Id))
        {
            return BadRequest(new { error = InputValidation.GetUserIdValidationError(request.Id) });
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "Name is required" });
        }

        var email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email;

        if (!Enum.TryParse<UserRole>(request.Role, true, out var role))
        {
            return BadRequest(new { error = "Valid role is required (Admin or FeedOwner)" });
        }

        var ownedFeeds = request.OwnedFeeds?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? [];

        var user = new User
        {
            Id = request.Id,
            Name = request.Name,
            Email = email,
            Role = role,
            OwnedFeeds = ownedFeeds,
            ApiKeyHash = "" // Will be set by UserService
        };

        try
        {
            var apiKey = await _userService.CreateUserAsync(user);
            _logger.LogInformation("Created user '{UserId}' with role '{Role}'", user.Id, user.Role);

            return CreatedAtAction(nameof(GetUser), new { userId = user.Id }, new
            {
                message = $"User '{user.Id}' created successfully",
                user = new
                {
                    user.Id,
                    user.Name,
                    user.Email,
                    user.Role,
                    user.OwnedFeeds
                },
                apiKey,
                warning = "This API key will only be shown once. Save it securely!"
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{userId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteUser(string userId)
    {
        var deleted = await _userService.DeleteUserAsync(userId);
        if (!deleted)
        {
            return NotFound(new { error = $"User '{userId}' not found" });
        }

        _logger.LogInformation("Deleted user '{UserId}'", userId);
        return Ok(new { message = $"User '{userId}' deleted" });
    }

    [HttpPost("{userId}/key/regenerate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RegenerateApiKey(string userId)
    {
        var newApiKey = await _userService.RegenerateApiKeyAsync(userId);
        if (newApiKey == null)
        {
            return NotFound(new { error = $"User '{userId}' not found" });
        }

        _logger.LogInformation("Regenerated API key for user '{UserId}'", userId);

        return Ok(new
        {
            message = $"API key regenerated for user '{userId}'",
            apiKey = newApiKey,
            warning = "This API key will only be shown once. Save it securely!"
        });
    }

    [HttpPost("{userId}/feeds")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GrantFeedOwnership(string userId, [FromBody] GrantFeedOwnershipRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FeedId))
        {
            return BadRequest(new { error = "feedId is required in request body" });
        }

        var feedId = request.FeedId;
        var granted = await _userService.GrantFeedOwnershipAsync(userId, feedId);

        if (!granted)
        {
            return BadRequest(new { error = $"Could not grant feed ownership. User '{userId}' not found or not a FeedOwner." });
        }

        _logger.LogInformation("Granted feed '{FeedId}' ownership to user '{UserId}'", feedId, userId);
        return Ok(new { message = $"Feed '{feedId}' ownership granted to user '{userId}'" });
    }

    [HttpDelete("{userId}/feeds/{feedId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RevokeFeedOwnership(string userId, string feedId)
    {
        var revoked = await _userService.RevokeFeedOwnershipAsync(userId, feedId);

        if (!revoked)
        {
            return BadRequest(new { error = $"Could not revoke feed ownership. User '{userId}' not found or not a FeedOwner." });
        }

        _logger.LogInformation("Revoked feed '{FeedId}' ownership from user '{UserId}'", feedId, userId);
        return Ok(new { message = $"Feed '{feedId}' ownership revoked from user '{userId}'" });
    }
}
