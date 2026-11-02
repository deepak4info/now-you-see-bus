using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using BusTrack.Api.Auth;
using BusTrack.Api.Controllers;
using BusTrack.Api.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace BusTrack.Api.Tests;

public class AuthAndSecurityTests
{
    private readonly IConfiguration _config;
    private const string TestKey = "TestSecretSigningKeyForWhereIsMyBusTesting12345!";
    private const string Issuer = "WhereIsMyBusTest";
    private const string Audience = "WhereIsMyBusAudience";

    public AuthAndSecurityTests()
    {
        var settings = new Dictionary<string, string?>
        {
            { "Jwt:Key", TestKey },
            { "Jwt:Issuer", Issuer },
            { "Jwt:Audience", Audience }
        };
        _config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    [Fact]
    public void JwtIssuer_GeneratesValidToken_WithAllUserClaims()
    {
        // Arrange
        var jwtIssuer = new JwtIssuer(_config);
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = "driver1@bus.local",
            FullName = "Rahul Driver",
            Role = "Driver",
            DriverCode = "DRV-RAHUL",
            IsActive = true
        };

        // Act
        var tokenString = jwtIssuer.Create(user);

        // Assert
        Assert.NotNull(tokenString);
        var handler = new JwtSecurityTokenHandler();
        var token = handler.ReadJwtToken(tokenString);

        Assert.Equal(Issuer, token.Issuer);
        Assert.Contains(Audience, token.Audiences);
        Assert.Equal(user.Id.ToString(), token.Claims.First(c => c.Type == ClaimTypes.NameIdentifier).Value);
        Assert.Equal(user.Email, token.Claims.First(c => c.Type == ClaimTypes.Email).Value);
        Assert.Equal(user.FullName, token.Claims.First(c => c.Type == ClaimTypes.Name).Value);
        Assert.Equal(user.Role, token.Claims.First(c => c.Type == ClaimTypes.Role).Value);
        Assert.Equal(user.DriverCode, token.Claims.First(c => c.Type == "driver_code").Value);
        Assert.True(token.ValidTo > DateTime.UtcNow);
    }

    [Fact]
    public void JwtIssuer_TokenCryptographicSignature_ValidatesSuccessfully()
    {
        // Arrange
        var jwtIssuer = new JwtIssuer(_config);
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = "admin@bus.local",
            FullName = "Admin User",
            Role = "Admin",
            IsActive = true
        };
        var tokenString = jwtIssuer.Create(user);

        // Act
        var validationParams = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = Issuer,
            ValidAudience = Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestKey))
        };

        var handler = new JwtSecurityTokenHandler();
        var principal = handler.ValidateToken(tokenString, validationParams, out var validatedToken);

        // Assert
        Assert.NotNull(principal);
        Assert.NotNull(validatedToken);
        Assert.True(principal.IsInRole("Admin"));
    }

    [Fact]
    public void JwtIssuer_TokenWithTamperedSignature_ThrowsSecurityException()
    {
        // Arrange
        var jwtIssuer = new JwtIssuer(_config);
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = "passenger@bus.local",
            FullName = "Test Passenger",
            Role = "Passenger",
            IsActive = true
        };
        var tokenString = jwtIssuer.Create(user);

        // Act & Assert: Validate with wrong key (tampered / forged token)
        var wrongKeyParams = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = Issuer,
            ValidAudience = Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("AnotherDifferentSecretKeyThatDoesNotMatch12345!"))
        };

        var handler = new JwtSecurityTokenHandler();
        Assert.Throws<SecurityTokenSignatureKeyNotFoundException>(() =>
            handler.ValidateToken(tokenString, wrongKeyParams, out _));
    }

    [Fact]
    public void PasswordHasher_HashesAndVerifiesPasswordCorrectly()
    {
        // Arrange
        var hasher = new PasswordHasher<User>();
        var user = new User { Id = Guid.NewGuid(), Email = "test@bus.local" };
        const string rawPassword = "SecurePassword@2026!";

        // Act
        var hash = hasher.HashPassword(user, rawPassword);

        // Assert
        Assert.NotNull(hash);
        Assert.NotEqual(rawPassword, hash); // Must never store plaintext
        Assert.True(hash.Length > 20);

        // Verify valid password
        var verifySuccess = hasher.VerifyHashedPassword(user, hash, rawPassword);
        Assert.Equal(PasswordVerificationResult.Success, verifySuccess);

        // Verify invalid password fails
        var verifyFail = hasher.VerifyHashedPassword(user, hash, "WrongPassword@123");
        Assert.Equal(PasswordVerificationResult.Failed, verifyFail);
    }

    [Fact]
    public void PasswordHasher_UsesSalt_ProducingDifferentHashesForSamePassword()
    {
        // Arrange
        var hasher = new PasswordHasher<User>();
        var user1 = new User { Id = Guid.NewGuid(), Email = "user1@bus.local" };
        var user2 = new User { Id = Guid.NewGuid(), Email = "user2@bus.local" };
        const string password = "IdenticalPassword@123";

        // Act
        var hash1 = hasher.HashPassword(user1, password);
        var hash2 = hasher.HashPassword(user2, password);

        // Assert
        Assert.NotEqual(hash1, hash2);
    }

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("@SUM(A1:A10)", "'@SUM(A1:A10)")]
    [InlineData("+cmd|'/c calc'!A0", "'+cmd|'/c calc'!A0")]
    [InlineData("-2+3*cmd", "'-2+3*cmd")]
    [InlineData("-50", "-50")] // Negative number should not be altered
    [InlineData("Fatehabad Bus Stand", "Fatehabad Bus Stand")]
    [InlineData("  Hisar  ", "Hisar")]
    public void SanitizeCsvCell_NeutralizesFormulaInjection(string input, string expected)
    {
        var sanitized = BulkUploadController.SanitizeCsvCell(input);
        Assert.Equal(expected, sanitized);
    }
}
