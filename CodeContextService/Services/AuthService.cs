using System.Net.Http.Headers;
using CodeContextService.Model;
using Microsoft.AspNetCore.Components;

namespace CodeContextService.Services;

public class AuthService
{
    private readonly HttpClient _http;
    private string? _token;

    public event Action AuthenticationStateChanged = () => { };

    // Inject NavigationManager so we know the current origin
    public AuthService(HttpClient http, NavigationManager nav)
    {
        // Blazor Server’s HttpClient has no BaseAddress by default
        http.BaseAddress = new Uri(nav.BaseUri);
        _http = http;
    }

    public string? Token => _token;
    public bool IsLoggedIn => !string.IsNullOrEmpty(_token);

    public async Task<bool> LoginAsync(string username, string password)
    {
        // now this will go to https://yourhost/api/auth/login
        var resp = await _http.PostAsJsonAsync("api/auth/login",
            new LoginRequest(username, password)
        );

        if (!resp.IsSuccessStatusCode)
            return false;

        var login = await resp.Content.ReadFromJsonAsync<LoginResponse>()!;
        _token = login.Token;

        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _token);

        AuthenticationStateChanged();
        return true;
    }

    public void Logout()
    {
        _token = null;
        _http.DefaultRequestHeaders.Authorization = null!;
        AuthenticationStateChanged();
    }
}
