using Microsoft.AspNetCore.Components.Authorization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace CodeContextService.Services;

public class JwtAuthenticationStateProvider : AuthenticationStateProvider
{
    private readonly AuthService _authService;

    public JwtAuthenticationStateProvider(AuthService authService)
    {
        _authService = authService;
        _authService.AuthenticationStateChanged += AuthService_AuthenticationStateChanged;
    }

    private void AuthService_AuthenticationStateChanged()
    {
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var token = _authService.Token;
        if (string.IsNullOrWhiteSpace(token))
        {
            var anon = new ClaimsPrincipal(new ClaimsIdentity());
            return Task.FromResult(new AuthenticationState(anon));
        }

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);
        var identity = new ClaimsIdentity(jwt.Claims, "jwt");
        var user = new ClaimsPrincipal(identity);
        return Task.FromResult(new AuthenticationState(user));
    }
}