namespace CodeContextService.Model;

public record JwtSettings(
    string Key,
    string Issuer,
    string Audience,
    int ExpiresMinutes
);

public record UserCredential(
    string Username,
    string Password,
    string Role
);

public class LoginRequest
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    // Parameterless ctor needed by Blazor binding
    public LoginRequest() { }

    public LoginRequest(string username, string password)
    {
        Username = username;
        Password = password;
    }
}

public record LoginResponse(
    string Token, 
    DateTime Expires);