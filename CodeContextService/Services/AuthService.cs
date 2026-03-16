using System.Net.Http.Headers;
using CodeContextService.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.JSInterop;

namespace CodeContextService.Services;

public class AuthService
{
    public const string AuthCookieName = "authToken";

    private readonly HttpClient _http;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly NavigationManager? _nav;
    private string? _token;
    private bool _triedRestoreFromCookie;
    private bool _baseAddressSet;

    public event Action AuthenticationStateChanged = () => { };

    public AuthService(HttpClient http, IHttpContextAccessor? httpContextAccessor = null, NavigationManager? nav = null)
    {
        _http = http;
        _httpContextAccessor = httpContextAccessor;
        _nav = nav;
        EnsureBaseAddress();
    }

    private void EnsureBaseAddress()
    {
        if (_baseAddressSet || _http.BaseAddress != null) return;
        var baseUri = (string?)null;
        try
        {
            baseUri = _nav?.BaseUri;
        }
        catch (InvalidOperationException)
        {
            // RemoteNavigationManager not initialized yet (e.g. during Blazor endpoint initial render); fall back to HttpContext
        }
        if (string.IsNullOrEmpty(baseUri))
        {
            var ctx = _httpContextAccessor?.HttpContext;
            if (ctx?.Request != null)
            {
                var scheme = ctx.Request.Scheme;
                var host = ctx.Request.Host.Value ?? "localhost";
                var pathBase = ctx.Request.PathBase.Value?.TrimEnd('/') ?? "";
                baseUri = $"{scheme}://{host}{pathBase}/";
            }
        }
        if (!string.IsNullOrEmpty(baseUri))
        {
            _http.BaseAddress = new Uri(baseUri);
            _baseAddressSet = true;
        }
    }

    public string? Token
    {
        get
        {
            if (_token != null) return _token;
            if (_triedRestoreFromCookie) return null;
            _triedRestoreFromCookie = true;
            try
            {
                var cookieToken = _httpContextAccessor?.HttpContext?.Request?.Cookies[AuthCookieName];
                if (!string.IsNullOrWhiteSpace(cookieToken))
                {
                    _token = cookieToken;
                    _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                }
            }
            catch
            {
                // Cookie/HttpContext may be unavailable in some circuit contexts
            }
            return _token;
        }
    }
    public bool IsLoggedIn => !string.IsNullOrEmpty(_token);

    public async Task<bool> LoginAsync(string username, string password)
    {
        EnsureBaseAddress();
        var resp = await _http.PostAsJsonAsync("api/auth/login",
            new LoginRequest(username, password)
        );

        if (!resp.IsSuccessStatusCode)
            return false;

        var login = await resp.Content.ReadFromJsonAsync<LoginResponse>();
        if (login?.Token == null)
            return false;

        _token = login.Token;
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _token);

        AuthenticationStateChanged();
        return true;
    }

    /// <summary>Write token to cookie so a full page reload (forceLoad) will restore auth. Call before NavigateTo(..., forceLoad: true).</summary>
    public async Task SetAuthCookieForReloadAsync(IJSRuntime js)
    {
        if (_token != null)
            await js.InvokeVoidAsync("setAuthToken", _token);
    }

    public void Logout()
    {
        _token = null;
        _triedRestoreFromCookie = false;
        _http.DefaultRequestHeaders.Authorization = null!;
        AuthenticationStateChanged();
    }
}
