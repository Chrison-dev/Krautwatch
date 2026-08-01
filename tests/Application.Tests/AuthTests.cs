using Krautwatch.Application.Auth;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Interfaces;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Krautwatch.Application.Tests;

public class SetupTokenTests
{
    [Fact]
    public void Generates_a_long_url_safe_token()
    {
        var token = new SetupToken().Value;

        token.Length.ShouldBeGreaterThanOrEqualTo(26);           // 160 bits, base64url
        token.ShouldNotContain("+");
        token.ShouldNotContain("/");
        token.ShouldNotContain("=");
    }

    [Fact]
    public void Two_instances_do_not_share_a_token()
    {
        new SetupToken().Value.ShouldNotBe(new SetupToken().Value);
    }

    [Fact]
    public void Matches_only_the_exact_token()
    {
        var token = new SetupToken();

        token.Matches(token.Value).ShouldBeTrue();
        token.Matches(token.Value + "x").ShouldBeFalse();
        token.Matches(token.Value[..^1]).ShouldBeFalse();
        token.Matches("").ShouldBeFalse();
        token.Matches(null).ShouldBeFalse();
    }
}

public class SignInHandlerTests
{
    private static AdminAccount Admin(string username = "admin", string hash = "stored-hash") => new()
    {
        Id = 1, Username = username, PasswordHash = hash,
    };

    [Fact]
    public async Task Signs_in_with_correct_credentials_and_stamps_last_login()
    {
        var store = Substitute.For<ILocalCredentialStore>();
        var hasher = Substitute.For<IPasswordHasher>();
        var admin = Admin();
        store.GetAsync(Arg.Any<CancellationToken>()).Returns(admin);
        hasher.Verify("stored-hash", "correct").Returns(PasswordVerification.Success);

        var result = await new SignInHandler(store, hasher)
            .HandleAsync(new SignInRequest("admin", "correct"), TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeTrue();
        result.Username.ShouldBe("admin");
        admin.LastLoginAt.ShouldNotBeNull();
        await store.Received(1).UpdateAsync(admin, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rejects_a_wrong_password_without_revealing_why()
    {
        var store = Substitute.For<ILocalCredentialStore>();
        var hasher = Substitute.For<IPasswordHasher>();
        store.GetAsync(Arg.Any<CancellationToken>()).Returns(Admin());
        hasher.Verify(Arg.Any<string>(), Arg.Any<string>()).Returns(PasswordVerification.Failed);

        var result = await new SignInHandler(store, hasher)
            .HandleAsync(new SignInRequest("admin", "wrong"), TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeFalse();
        result.Username.ShouldBeNull();
    }

    [Fact]
    public async Task Rejects_an_unknown_username()
    {
        var store = Substitute.For<ILocalCredentialStore>();
        var hasher = Substitute.For<IPasswordHasher>();
        store.GetAsync(Arg.Any<CancellationToken>()).Returns(Admin("admin"));

        var result = await new SignInHandler(store, hasher)
            .HandleAsync(new SignInRequest("someone-else", "correct"), TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeFalse();
        // The password must not even be checked for an unknown user.
        hasher.DidNotReceive().Verify(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Username_comparison_is_case_insensitive()
    {
        var store = Substitute.For<ILocalCredentialStore>();
        var hasher = Substitute.For<IPasswordHasher>();
        store.GetAsync(Arg.Any<CancellationToken>()).Returns(Admin("Admin"));
        hasher.Verify(Arg.Any<string>(), Arg.Any<string>()).Returns(PasswordVerification.Success);

        var result = await new SignInHandler(store, hasher)
            .HandleAsync(new SignInRequest("ADMIN", "correct"), TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task Fails_when_no_administrator_exists()
    {
        var store = Substitute.For<ILocalCredentialStore>();
        store.GetAsync(Arg.Any<CancellationToken>()).Returns((AdminAccount?)null);

        var result = await new SignInHandler(store, Substitute.For<IPasswordHasher>())
            .HandleAsync(new SignInRequest("admin", "whatever"), TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task Rehashes_when_the_stored_hash_is_outdated()
    {
        var store = Substitute.For<ILocalCredentialStore>();
        var hasher = Substitute.For<IPasswordHasher>();
        var admin = Admin(hash: "old-format");
        store.GetAsync(Arg.Any<CancellationToken>()).Returns(admin);
        hasher.Verify("old-format", "correct").Returns(PasswordVerification.SuccessRehashNeeded);
        hasher.Hash("correct").Returns("new-format");

        var result = await new SignInHandler(store, hasher)
            .HandleAsync(new SignInRequest("admin", "correct"), TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeTrue();
        admin.PasswordHash.ShouldBe("new-format");
    }
}

public class CreateAdminHandlerTests
{
    [Fact]
    public async Task Creates_the_administrator_with_a_hashed_password()
    {
        var store = Substitute.For<ILocalCredentialStore>();
        var hasher = Substitute.For<IPasswordHasher>();
        store.ExistsAsync(Arg.Any<CancellationToken>()).Returns(false);
        hasher.Hash("a-long-enough-password").Returns("hashed");

        var created = await new CreateAdminHandler(store, hasher).HandleAsync(
            new CreateAdminRequest("admin", "a-long-enough-password", "a-long-enough-password"),
            TestContext.Current.CancellationToken);

        created.ShouldBeTrue();
        await store.Received(1).CreateAsync(
            Arg.Is<AdminAccount>(a => a != null && a.Username == "admin" && a.PasswordHash == "hashed"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refuses_when_an_administrator_already_exists()
    {
        // The takeover guard: a replayed setup POST must not overwrite the existing admin.
        var store = Substitute.For<ILocalCredentialStore>();
        store.ExistsAsync(Arg.Any<CancellationToken>()).Returns(true);

        var created = await new CreateAdminHandler(store, Substitute.For<IPasswordHasher>()).HandleAsync(
            new CreateAdminRequest("attacker", "a-long-enough-password", "a-long-enough-password"),
            TestContext.Current.CancellationToken);

        created.ShouldBeFalse();
        await store.DidNotReceive().CreateAsync(Arg.Any<AdminAccount>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Never_stores_the_plaintext_password()
    {
        var store = Substitute.For<ILocalCredentialStore>();
        var hasher = Substitute.For<IPasswordHasher>();
        store.ExistsAsync(Arg.Any<CancellationToken>()).Returns(false);
        hasher.Hash(Arg.Any<string>()).Returns("hashed");

        await new CreateAdminHandler(store, hasher).HandleAsync(
            new CreateAdminRequest("admin", "correct horse battery", "correct horse battery"),
            TestContext.Current.CancellationToken);

        await store.Received(1).CreateAsync(
            Arg.Is<AdminAccount>(a => a != null && a.PasswordHash != "correct horse battery"),
            Arg.Any<CancellationToken>());
    }
}

public class CreateAdminRequestValidatorTests
{
    private static readonly CreateAdminRequestValidator Validator = new();

    [Theory]
    [InlineData("", "a-long-enough-password")]           // no username
    [InlineData("admin", "")]                            // no password at all
    [InlineData("admin", "   ")]                         // whitespace is not a password
    public void Rejects_invalid_input(string username, string password)
    {
        Validator.Validate(new CreateAdminRequest(username, password, password)).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Rejects_mismatched_confirmation()
    {
        Validator.Validate(new CreateAdminRequest("admin", "a-long-enough-password", "something-else"))
            .IsValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData("a-long-enough-password")]
    [InlineData("short")]
    [InlineData("x")]
    [InlineData("1234")]
    [InlineData("hunter2")]
    public void Accepts_whatever_password_the_operator_chose(string password)
    {
        // No minimum length and no character-class rules by design: this is a single-admin credential on a
        // box the operator already controls, and a policy that blocks the password they wanted just moves
        // it onto a sticky note. Real password policy belongs at an identity provider via Auth:Provider=oidc.
        Validator.Validate(new CreateAdminRequest("admin", password, password)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Still_bounds_the_length_for_storage()
    {
        var tooLong = new string('x', 257);
        Validator.Validate(new CreateAdminRequest("admin", tooLong, tooLong)).IsValid.ShouldBeFalse();
    }
}
