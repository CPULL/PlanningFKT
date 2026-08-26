using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using minerva.planningfkt.models;

namespace minerva.planningfkt.controllers;

public partial class AppController {
  public class LoginRequest {
    public string Name { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
  }

  [HttpPost("login")]
  [AllowAnonymous]
  public async Task<IActionResult> Login([FromBody] LoginRequest request) {
    var therapist = _db.Therapists.FirstOrDefault(t => t.Name == request.Name);

    if (therapist == null || therapist.PasswordHash == null) {
      return Unauthorized();
    }

    var hasher = new PasswordHasher<Therapist>();
    var result = hasher.VerifyHashedPassword(therapist, therapist.PasswordHash, request.Password);

    if (result == PasswordVerificationResult.Failed) {
      return Unauthorized();
    }

    var claims = new List<Claim> {
      new Claim(ClaimTypes.NameIdentifier, therapist.Id.ToString()),
      new Claim(ClaimTypes.Name, therapist.Name)
    };

    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    var principal = new ClaimsPrincipal(identity);

    await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

    return Ok(new { name = therapist.Name });
  }

  [HttpPost("logout")]
  public async Task<IActionResult> Logout() {
    await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Ok();
  }

  [HttpGet("me")]
  public IActionResult Me() {
    var idClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    if (idClaim == null || !int.TryParse(idClaim, out var id)) {
      return Unauthorized();
    }

    var therapist = _db.Therapists.Find(id);

    if (therapist == null) {
      return Unauthorized();
    }

    var isAccettazione = (therapist.OperatingArea & TherapistOperatingArea.Accettazione) != 0;

    return Ok(new { id = therapist.Id, name = therapist.Name, isAccettazione });
  }
}
