using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using CloudLink.Core;

namespace CloudLink.Tests;

public sealed class OAuthTests : IDisposable
{
    readonly FakeServer _server = new();
    readonly HttpClient _http = new();
    readonly string _dir = Path.Combine(Path.GetTempPath(), "cloudlink-tests-" + Guid.NewGuid().ToString("N"));
    readonly string _tokenFile;
    string _clientId = "11111111-1111-1111-1111-111111111111";
    string _tokenReply = """{"access_token":"access-1","expires_in":3600,"refresh_token":"refresh-2"}""";
    int _tokenStatus = 200;
    readonly List<string> _forms = [];

    public OAuthTests()
    {
        Directory.CreateDirectory(_dir);
        AppPaths.Root = Path.Combine(_dir, "appdata");
        // An absolute name, so the file stays put whatever other test classes do with AppPaths.Root.
        _tokenFile = Path.Combine(_dir, "test.token");
        _server.Route("/token", async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.InputStream);
            string form = await reader.ReadToEndAsync();
            lock (_forms) _forms.Add(form);
            await FakeServer.WriteAsync(ctx, _tokenStatus, _tokenReply, "application/json");
        });
    }

    public void Dispose()
    {
        _server.Dispose();
        _http.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    OAuthSession Session() => new(() => new OAuthConfig("Test", _server.Base + "/authorize", _server.Base + "/token",
        _clientId, null, "scope", "localhost", new Dictionary<string, string>()), _tokenFile, _http);

    void Store(string text) =>
        File.WriteAllBytes(_tokenFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(text), null, DataProtectionScope.CurrentUser));

    string Stored() =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(_tokenFile), null, DataProtectionScope.CurrentUser));

    [Fact]
    public void Not_signed_in_without_a_stored_sign_in() => Assert.False(Session().IsSignedIn);

    [Fact]
    public async Task A_sign_in_stored_by_an_older_version_still_works_and_is_rebound_to_the_application()
    {
        Store("refresh-1");
        var session = Session();
        Assert.True(session.IsSignedIn);

        Assert.Equal("access-1", await session.GetAccessTokenAsync(default));
        Assert.Contains("refresh_token=refresh-1", Assert.Single(_forms));
        Assert.Contains("client_id=" + _clientId, _forms[0]);
        // The rotated token is saved together with the application it belongs to.
        Assert.Contains("\"refresh_token\":\"refresh-2\"", Stored());
        Assert.Contains(_clientId, Stored());

        // The access token is reused until it nears expiry.
        Assert.Equal("access-1", await session.GetAccessTokenAsync(default));
        Assert.Single(_forms);
    }

    [Fact]
    public async Task A_sign_in_made_through_another_application_is_not_used()
    {
        Store("""{"client_id":"99999999-9999-9999-9999-999999999999","refresh_token":"refresh-1"}""");
        var session = Session();
        Assert.False(session.IsSignedIn);
        await Assert.ThrowsAsync<SignInRequiredException>(() => session.GetAccessTokenAsync(default));
        Assert.Empty(_forms);                 // the token was never sent anywhere
        Assert.True(File.Exists(_tokenFile)); // reading does not delete; ForgetIfForeign and Settings do
    }

    [Fact]
    public void A_sign_in_left_by_another_application_is_deleted_on_request()
    {
        Store("""{"client_id":"99999999-9999-9999-9999-999999999999","refresh_token":"refresh-1"}""");
        Session().ForgetIfForeign();
        Assert.False(File.Exists(_tokenFile));
    }

    [Theory]
    [InlineData("refresh-1")]                                                                         // from an older version: owner unknown
    [InlineData("""{"client_id":"abcdef00-1111-1111-1111-111111111111","refresh_token":"refresh-1"}""")]
    [InlineData("""{"client_id":"ABCDEF00-1111-1111-1111-111111111111","refresh_token":"refresh-1"}""")]   // same ID, other case
    public void A_sign_in_that_may_be_ours_is_not_deleted(string stored)
    {
        _clientId = "abcdef00-1111-1111-1111-111111111111";
        Store(stored);
        var session = Session();
        session.ForgetIfForeign();
        Assert.True(File.Exists(_tokenFile));
        Assert.True(session.IsSignedIn);
    }

    [Fact]
    public async Task An_older_sign_in_is_bound_to_the_application_even_when_the_token_is_not_replaced()
    {
        // Google answers a refresh without a new refresh token.
        Store("refresh-1");
        _tokenReply = """{"access_token":"access-1","expires_in":3600}""";
        Assert.Equal("access-1", await Session().GetAccessTokenAsync(default));
        Assert.Contains("\"refresh_token\":\"refresh-1\"", Stored());
        Assert.Contains(_clientId, Stored());
    }

    [Theory]
    [InlineData(400, "invalid_request")]
    [InlineData(401, "invalid_client")]
    [InlineData(400, "unauthorized_client")]
    public async Task An_older_sign_in_that_is_refused_for_any_reason_is_dropped(int status, string error)
    {
        // It carries no application ID, so a refusal may simply mean it belongs to another application.
        Store("refresh-1");
        _tokenStatus = status;
        _tokenReply = $$"""{"error":"{{error}}","error_description":"refused"}""";
        var session = Session();
        await Assert.ThrowsAsync<SignInRequiredException>(() => session.GetAccessTokenAsync(default));
        Assert.False(File.Exists(_tokenFile));
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task An_older_sign_in_survives_a_busy_token_service(int status)
    {
        Store("refresh-1");
        _tokenStatus = status;
        _tokenReply = """{"error":"temporarily_unavailable"}""";
        var session = Session();
        var failure = await Assert.ThrowsAsync<CloudException>(() => session.GetAccessTokenAsync(default));
        Assert.True(failure.Transient);
        Assert.True(File.Exists(_tokenFile));
    }

    [Fact]
    public async Task Without_an_application_id_nothing_is_signed_in_and_sign_in_explains_why()
    {
        Store("refresh-1");
        _clientId = "";
        var session = Session();
        Assert.False(session.IsSignedIn);
        await Assert.ThrowsAsync<SignInRequiredException>(() => session.GetAccessTokenAsync(default));
        var error = await Assert.ThrowsAsync<CloudException>(() => session.SignInAsync(default));
        Assert.Contains("Settings", error.Message);
        Assert.Empty(_forms);
    }

    [Theory]
    [InlineData("invalid_client")]
    [InlineData("unauthorized_client")]
    public async Task A_problem_with_the_application_does_not_cost_the_sign_in(string error)
    {
        // A wrong client secret or a registration being changed: the sign-in works again once that is put right.
        Store("""{"client_id":"11111111-1111-1111-1111-111111111111","refresh_token":"refresh-1"}""");
        _tokenStatus = 401;
        _tokenReply = $$"""{"error":"{{error}}","error_description":"bad client"}""";
        var session = Session();
        await Assert.ThrowsAsync<CloudException>(() => session.GetAccessTokenAsync(default));
        Assert.True(session.IsSignedIn);
        Assert.True(File.Exists(_tokenFile));
    }

    [Theory]
    [InlineData("invalid_grant")]
    [InlineData("interaction_required")]
    public async Task A_refused_sign_in_is_dropped_and_a_new_one_is_asked_for(string error)
    {
        Store("""{"client_id":"11111111-1111-1111-1111-111111111111","refresh_token":"refresh-1"}""");
        _tokenStatus = 400;
        _tokenReply = $$"""{"error":"{{error}}","error_description":"AADSTS70000: refused"}""";
        var session = Session();
        await Assert.ThrowsAsync<SignInRequiredException>(() => session.GetAccessTokenAsync(default));
        Assert.False(session.IsSignedIn);
        Assert.False(File.Exists(_tokenFile));
    }

    [Fact]
    public async Task A_passing_error_from_the_token_service_keeps_the_sign_in()
    {
        Store("""{"client_id":"11111111-1111-1111-1111-111111111111","refresh_token":"refresh-1"}""");
        _tokenStatus = 400;
        _tokenReply = """{"error":"temporarily_unavailable","error_description":"try later; this text mentions invalid_grant only in passing"}""";
        var session = Session();
        await Assert.ThrowsAsync<CloudException>(() => session.GetAccessTokenAsync(default));
        Assert.True(session.IsSignedIn);
        Assert.True(File.Exists(_tokenFile));
    }

    [Fact]
    public void Sign_out_forgets_the_stored_sign_in()
    {
        Store("refresh-1");
        var session = Session();
        Assert.True(session.IsSignedIn);
        session.SignOut();
        Assert.False(session.IsSignedIn);
        Assert.False(File.Exists(_tokenFile));
    }

    [Theory]
    [InlineData("access_denied", "AADSTS90094: Admin consent is required for the permissions requested by this application.", "administrator")]
    [InlineData("access_denied", "AADSTS900941: An administrator of contoso.com must approve this app.", "administrator")]
    [InlineData("access_denied", "AADSTS65004: User declined to consent to access the app.", "cancelled")]
    [InlineData("access_denied", null, "cancelled")]
    [InlineData("invalid_request", "AADSTS50011: The redirect URI does not match. Trace ID: 1234 Correlation ID: 5678", "AADSTS50011: The redirect URI does not match.")]
    [InlineData("server_error", null, "server_error")]
    public void Sign_in_errors_are_explained(string error, string? description, string expected)
    {
        string message = OAuthSession.DescribeSignInError("Microsoft", error, description);
        Assert.Contains(expected, message);
        Assert.DoesNotContain("Trace ID", message);
    }

    [Fact]
    public void The_application_id_set_for_the_build_reaches_the_program()
    {
        // The test project is given the same build property, so a broken hand-over shows up as a mismatch.
        string? configured = typeof(OAuthTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "ExpectedMicrosoftClientId").Value;
        Assert.Equal(Settings.CanonicalId(configured), Settings.BuiltInMicrosoftClientId);
    }

    [Theory]
    [InlineData("{22222222-2222-2222-2222-22222222ABCD}", "22222222-2222-2222-2222-22222222abcd")]
    [InlineData("2222222222222222222222222222ABCD", "22222222-2222-2222-2222-22222222abcd")]
    [InlineData("not an id", "")]
    [InlineData(null, "")]
    public void Application_ids_are_brought_to_one_form(string? typed, string expected) =>
        Assert.Equal(expected, Settings.CanonicalId(typed));

    [Fact]
    public void Something_that_is_not_an_id_in_settings_falls_back_to_the_built_in_one() =>
        Assert.Equal(Settings.BuiltInMicrosoftClientId, new Settings { MicrosoftClientId = "not an id" }.EffectiveMicrosoftClientId);

    [Fact]
    public void An_application_id_entered_in_settings_wins_over_the_built_in_one()
    {
        var settings = new Settings { MicrosoftClientId = "  22222222-2222-2222-2222-222222222222 " };
        Assert.Equal("22222222-2222-2222-2222-222222222222", settings.EffectiveMicrosoftClientId);
        Assert.True(settings.HasMicrosoftClientId);
        Assert.Equal(Settings.BuiltInMicrosoftClientId, new Settings().EffectiveMicrosoftClientId);
    }
}
