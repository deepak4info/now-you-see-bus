using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using BusTrack.Api.Data;
using Microsoft.IdentityModel.Tokens;

namespace BusTrack.Api.Auth;

public class JwtIssuer
{
    private readonly IConfiguration _config;

    public JwtIssuer(IConfiguration config) => _config = config;

    public string Create(User user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_config["Jwt:Key"]!));
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Name, user.FullName),
            new(ClaimTypes.Role, user.Role)
        };
        if (!string.IsNullOrWhiteSpace(user.DriverCode))
            claims.Add(new Claim("driver_code", user.DriverCode));

        var token = new JwtSecurityToken(
            issuer: _config["Jwt:Issuer"],
            audience: _config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddHours(12),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public static class UserClaims
{
    public static Guid Id(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
