using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Interfaces;
using Krautwatch.Infrastructure.Auth;
using Krautwatch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Krautwatch.Infrastructure.Tests;

public class IdentityPasswordHasherTests
{
    private readonly IPasswordHasher _sut = new IdentityPasswordHasher();

    [Fact]
    public void Verifies_a_correct_password()
    {
        var hash = _sut.Hash("correct horse battery staple");

        _sut.Verify(hash, "correct horse battery staple").ShouldBe(PasswordVerification.Success);
    }

    [Fact]
    public void Rejects_a_wrong_password()
    {
        var hash = _sut.Hash("correct horse battery staple");

        _sut.Verify(hash, "Correct horse battery staple").ShouldBe(PasswordVerification.Failed);
        _sut.Verify(hash, "").ShouldBe(PasswordVerification.Failed);
    }

    [Fact]
    public void Hash_does_not_contain_the_password()
    {
        _sut.Hash("correct horse battery staple").ShouldNotContain("correct");
    }

    [Fact]
    public void Same_password_hashes_differently_each_time()
    {
        // If these ever match, salting has stopped happening and the hashes became rainbow-table food.
        _sut.Hash("same password").ShouldNotBe(_sut.Hash("same password"));
    }
}

[Collection(PostgresCollection.Name)]
public class LocalCredentialStoreTests(PostgresFixture postgres) : IAsyncLifetime
{
    private DbContextOptions<AppDbContext> _options = null!;

    public async ValueTask InitializeAsync() => _options = await postgres.CreateDatabaseAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private LocalCredentialStore Store() => new(new AppDbContext(_options));

    private static AdminAccount NewAdmin(string username = "admin") => new()
    {
        Username = username,
        PasswordHash = "hashed",
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task No_administrator_exists_on_a_fresh_database()
    {
        // This is what triggers first-run setup — and confirms no default account is ever seeded.
        (await Store().ExistsAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await Store().GetAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task Creates_then_reads_back_the_administrator()
    {
        await Store().CreateAsync(NewAdmin(), TestContext.Current.CancellationToken);

        (await Store().ExistsAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        var admin = await Store().GetAsync(TestContext.Current.CancellationToken);
        admin.ShouldNotBeNull();
        admin.Username.ShouldBe("admin");
        admin.Id.ShouldBe(1); // singleton row
    }

    [Fact]
    public async Task A_second_create_cannot_replace_the_first_administrator()
    {
        await Store().CreateAsync(NewAdmin("first"), TestContext.Current.CancellationToken);

        // The singleton primary key is the backstop behind the handler's ExistsAsync check: even a
        // racing setup POST cannot insert a second admin.
        await Should.ThrowAsync<DbUpdateException>(async () =>
            await Store().CreateAsync(NewAdmin("attacker"), TestContext.Current.CancellationToken));

        var admin = await Store().GetAsync(TestContext.Current.CancellationToken);
        admin!.Username.ShouldBe("first");
    }

    [Fact]
    public async Task Updates_the_last_login_timestamp()
    {
        await Store().CreateAsync(NewAdmin(), TestContext.Current.CancellationToken);

        var admin = await Store().GetAsync(TestContext.Current.CancellationToken);
        admin!.LastLoginAt.ShouldBeNull();

        var stamp = DateTimeOffset.UtcNow;
        admin.LastLoginAt = stamp;
        await Store().UpdateAsync(admin, TestContext.Current.CancellationToken);

        var reread = await Store().GetAsync(TestContext.Current.CancellationToken);
        reread!.LastLoginAt.ShouldNotBeNull();
    }
}
