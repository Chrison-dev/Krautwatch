using Krautwatch.Application.Settings;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Interfaces;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Krautwatch.Application.Tests;

/// <summary>
/// Covers first-run setup state (#54). The behaviour that matters is that completion is tracked
/// <em>separately</em> from "an administrator exists" — inferring it from the admin account cannot tell
/// a half-finished wizard apart from a finished one, so an interrupted setup would restart rather than
/// resume.
/// </summary>
public class SetupWizardTests
{
    private readonly ISettingsRepository _settings = Substitute.For<ISettingsRepository>();
    private readonly ILocalCredentialStore _credentials = Substitute.For<ILocalCredentialStore>();

    private void Given(DateTimeOffset? completedAt, bool adminExists)
    {
        _settings.GetAsync(Arg.Any<CancellationToken>())
            .Returns(new AppSettings { Id = 1, SetupCompletedAt = completedAt });
        _credentials.ExistsAsync(Arg.Any<CancellationToken>()).Returns(adminExists);
    }

    private Task<SetupState> State() =>
        new SetupWizardStateHandler(_settings, _credentials)
            .HandleAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task Setup_is_required_on_a_fresh_instance()
    {
        Given(completedAt: null, adminExists: false);

        var state = await State();

        state.Required.ShouldBeTrue();
        state.AdministratorExists.ShouldBeFalse();

        // Nothing past the administrator step is reachable without the session creating one produces.
        state.ResumeAt.ShouldBe(SetupStep.Welcome);
    }

    [Fact]
    public async Task An_admin_without_a_completed_wizard_resumes_rather_than_restarting()
    {
        // The state that "an administrator exists" alone cannot express, and the reason for the column.
        Given(completedAt: null, adminExists: true);

        var state = await State();

        state.Required.ShouldBeTrue();
        state.ResumeAt.ShouldBe(SetupStep.Database);
    }

    [Fact]
    public async Task Setup_is_not_required_once_completed()
    {
        Given(completedAt: DateTimeOffset.UtcNow.AddDays(-30), adminExists: true);

        (await State()).Required.ShouldBeFalse();
    }

    [Fact]
    public async Task A_completed_wizard_stays_completed_even_with_no_administrator()
    {
        // Reachable if the admin row is removed later (#68's territory). The wizard is not the place to
        // re-derive auth state, and re-running it would hand the instance to whoever asks.
        Given(completedAt: DateTimeOffset.UtcNow, adminExists: false);

        (await State()).Required.ShouldBeFalse();
    }

    [Fact]
    public async Task Completing_stamps_the_settings_row()
    {
        var stored = new AppSettings { Id = 1, SetupCompletedAt = null };
        _settings.GetAsync(Arg.Any<CancellationToken>()).Returns(stored);

        await new CompleteSetupHandler(_settings).HandleAsync(TestContext.Current.CancellationToken);

        stored.SetupCompletedAt.ShouldNotBeNull();
        await _settings.Received(1).SaveAsync(stored, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Completing_twice_does_not_move_the_timestamp()
    {
        // A double submit or a refresh of the last step must not rewrite when this instance was set up.
        var original = DateTimeOffset.UtcNow.AddDays(-7);
        var stored = new AppSettings { Id = 1, SetupCompletedAt = original };
        _settings.GetAsync(Arg.Any<CancellationToken>()).Returns(stored);

        await new CompleteSetupHandler(_settings).HandleAsync(TestContext.Current.CancellationToken);

        stored.SetupCompletedAt.ShouldBe(original);
        await _settings.DidNotReceive().SaveAsync(Arg.Any<AppSettings>(), Arg.Any<CancellationToken>());
    }
}
