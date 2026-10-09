using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Resources;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Web.DTOs.ServiceRequests;
using Casazen.Web.DTOs.Supplier;
using Casazen.Web.Resources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>SP-04: the codes, the texts (Italian and English) and the options of the service request flows.</summary>
public class ServiceRequestSupportTests
{
    // ─── Codes and message keys ───

    [Fact]
    public void MessageKeys_EveryKeyOfTheServiceRequestFlows_ExistsInItalianAndEnglishWithDifferentText()
    {
        var italian = ReadEntries(CultureInfo.InvariantCulture);
        var english = ReadEntries(new CultureInfo("en"));

        Assert.All(ServiceRequestErrorCodes.MessageKeys, key =>
        {
            Assert.True(italian.ContainsKey(key), $"{key} is missing from SharedResources.resx");
            Assert.True(english.ContainsKey(key), $"{key} is missing from SharedResources.en.resx");
            Assert.False(string.IsNullOrWhiteSpace(italian[key]), key);
            Assert.False(string.IsNullOrWhiteSpace(english[key]), key);
            Assert.NotEqual(italian[key], english[key]);
        });
    }

    [Fact]
    public void MessageKeys_ListEveryMessageKeyConstantOnce()
    {
        var declared = typeof(ServiceRequestErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, FieldType.Name: nameof(String) } && f.Name.EndsWith("MessageKey", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Order()
            .ToList();

        Assert.Equal(declared, ServiceRequestErrorCodes.MessageKeys.Order());
        Assert.Equal(ServiceRequestErrorCodes.MessageKeys.Count, ServiceRequestErrorCodes.MessageKeys.Distinct().Count());
    }

    [Fact]
    public void Codes_AreUniqueSnakeCaseAndThereIsOneForEveryRefusalTheFrontendTellsApart()
    {
        var codes = typeof(ServiceRequestErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, FieldType.Name: nameof(String) } && !f.Name.EndsWith("MessageKey", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.All(codes, code => Assert.Matches("^[a-z]+(_[a-z]+)+$", code));
        foreach (var expected in new[]
                 {
                     "supplier_slot_unavailable", "service_request_time_needs_service", "service_request_time_invalid",
                     "service_request_amount_invalid", "service_request_final_amount_invalid", "service_request_remind_too_soon",
                     "service_request_no_proposal", "service_request_photo_invalid", "service_request_photo_limit_reached",
                     "service_request_photo_not_found", "service_request_invalid_transition", "service_request_state_changed",
                 })
        {
            Assert.Contains(expected, codes);
        }
    }

    [Fact]
    public void MessageKeys_TheMessagesThatNameANumberOrAFile_TakeTheirArguments()
    {
        var italian = ReadEntries(CultureInfo.InvariantCulture);
        var english = ReadEntries(new CultureInfo("en"));

        foreach (var (key, placeholders) in new[]
                 {
                     (ServiceRequestErrorCodes.RemindTooSoonMessageKey, new[] { "{0}" }),
                     (ServiceRequestErrorCodes.PhotoInvalidMessageKey, new[] { "{0}" }),
                     (ServiceRequestErrorCodes.PhotoLimitReachedMessageKey, new[] { "{0}", "{1}", "{2}" }),
                     (ServiceRequestErrorCodes.AmountInvalidMessageKey, new[] { "{0}", "{1}", "{2}" }),
                 })
        {
            foreach (var placeholder in placeholders)
            {
                Assert.Contains(placeholder, italian[key]);
                Assert.Contains(placeholder, english[key]);
            }
        }
    }

    [Fact]
    public void ValidationMessages_EveryErrorMessageOfTheNewRequestBodies_ExistsInItalianAndEnglishWithDifferentText()
    {
        var italian = ReadEntries(CultureInfo.InvariantCulture);
        var english = ReadEntries(new CultureInfo("en"));
        var keys = new[]
            {
                typeof(CreateServiceRequestRequest), typeof(TakeServiceRequestRequest), typeof(CompleteServiceRequestRequest),
                typeof(ServiceRequestExtraRequest), typeof(CancelServiceRequestRequest), typeof(ProposeServiceRequestTimeRequest),
                typeof(RejectServiceRequestRequest), typeof(AcceptServiceRequestsRequest),
            }
            .SelectMany(type => type.GetProperties())
            .SelectMany(property => property.GetCustomAttributes<ValidationAttribute>())
            .Select(attribute => attribute.ErrorMessage)
            .Where(message => !string.IsNullOrEmpty(message))
            .Select(message => message!)
            .Distinct()
            .ToList();

        Assert.NotEmpty(keys);
        Assert.All(keys, key =>
        {
            Assert.True(italian.ContainsKey(key), $"{key} is missing from SharedResources.resx");
            Assert.True(english.ContainsKey(key), $"{key} is missing from SharedResources.en.resx");
            Assert.NotEqual(italian[key], english[key]);
        });
    }

    [Fact]
    public void RequestBodies_TheLimitsAreTheOnesOfTheServiceRequestRules()
    {
        T Attribute<T>(Type type, string property)
            where T : Attribute => type.GetProperty(property)!.GetCustomAttribute<T>()!;

        Assert.Equal(ServiceRequestLimits.CancellationReasonMaxLength, Attribute<MaxLengthAttribute>(typeof(CancelServiceRequestRequest), nameof(CancelServiceRequestRequest.Reason)).Length);
        Assert.Equal(ServiceRequestLimits.ProposalMessageMaxLength, Attribute<MaxLengthAttribute>(typeof(ProposeServiceRequestTimeRequest), nameof(ProposeServiceRequestTimeRequest.Message)).Length);
        Assert.Equal(ServiceRequestLimits.MaxExtras, Attribute<MaxLengthAttribute>(typeof(CompleteServiceRequestRequest), nameof(CompleteServiceRequestRequest.Extras)).Length);
        Assert.Equal(ServiceRequestLimits.MaxBatchAccept, Attribute<MaxLengthAttribute>(typeof(AcceptServiceRequestsRequest), nameof(AcceptServiceRequestsRequest.Ids)).Length);
        Assert.Equal(ServiceRequestLimits.MaxAmountCents, (int)Attribute<RangeAttribute>(typeof(TakeServiceRequestRequest), nameof(TakeServiceRequestRequest.QuotedAmountCents)).Maximum);
        Assert.Equal(1, (int)Attribute<RangeAttribute>(typeof(CompleteServiceRequestRequest), nameof(CompleteServiceRequestRequest.FinalAmountCents)).Minimum);
    }

    // ─── Options ───

    [Fact]
    public void Options_Defaults_AreTheDecisionsOfTheWaveSpec()
    {
        var options = new ServiceRequestOptions();

        Assert.Equal("Suppliers:ServiceRequests", ServiceRequestOptions.SectionName);
        Assert.Equal(120, options.HostResponseMinutes); // D8
        Assert.Equal(20, options.FinalAmountTolerancePercent); // D7
        Assert.Equal(6, options.RemindIntervalHours);
        Assert.Empty(options.Validate());
    }

    [Theory]
    [InlineData(10, 0, 1, true)]
    [InlineData(4320, 100, 72, true)]
    [InlineData(9, 20, 6, false)]
    [InlineData(4321, 20, 6, false)]
    [InlineData(120, -1, 6, false)]
    [InlineData(120, 101, 6, false)]
    [InlineData(120, 20, 0, false)]
    [InlineData(120, 20, 73, false)]
    public void Options_Validate_AcceptsTheRangesAndRefusesTheRest(int minutes, int tolerance, int hours, bool valid)
    {
        var options = new ServiceRequestOptions
        {
            HostResponseMinutes = minutes,
            FinalAmountTolerancePercent = tolerance,
            RemindIntervalHours = hours,
        };

        Assert.Equal(valid, options.Validate().Count == 0);
    }

    [Fact]
    public void Options_ABadValue_IsRefusedAtStartupNamingTheEnvironmentVariable()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Suppliers:ServiceRequests:HostResponseMinutes"] = "5",
                ["Suppliers:ServiceRequests:RemindIntervalHours"] = "0",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddOptions<ServiceRequestOptions>().Bind(configuration.GetSection(ServiceRequestOptions.SectionName));
        services.AddSingleton<IValidateOptions<ServiceRequestOptions>, ServiceRequestOptionsValidator>();
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<ServiceRequestOptions>>().Value);

        Assert.Contains(ex.Failures, failure => failure.Contains("Suppliers__ServiceRequests__HostResponseMinutes"));
        Assert.Contains(ex.Failures, failure => failure.Contains("Suppliers__ServiceRequests__RemindIntervalHours"));
        Assert.DoesNotContain(ex.Failures, failure => failure.Contains("FinalAmountTolerancePercent"));
    }

    [Fact]
    public void Options_FromTheConfiguration_ReplaceTheDefaults()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Suppliers:ServiceRequests:HostResponseMinutes"] = "180",
                ["Suppliers:ServiceRequests:FinalAmountTolerancePercent"] = "30",
                ["Suppliers:ServiceRequests:RemindIntervalHours"] = "12",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddOptions<ServiceRequestOptions>().Bind(configuration.GetSection(ServiceRequestOptions.SectionName));
        services.AddSingleton<IValidateOptions<ServiceRequestOptions>, ServiceRequestOptionsValidator>();
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<ServiceRequestOptions>>().Value;

        Assert.Equal(180, options.HostResponseMinutes);
        Assert.Equal(30, options.FinalAmountTolerancePercent);
        Assert.Equal(12, options.RemindIntervalHours);
    }

    // ─── Statuses and values that cross the API ───

    [Fact]
    public void Status_TheIntegersAreExplicitAndAnnullatoIsAppendedAfterRifiutato()
    {
        Assert.Equal(
            new[] { ("Richiesto", 0), ("PresoInCarico", 1), ("InCorso", 2), ("Completato", 3), ("Pagato", 4), ("Rifiutato", 5), ("Annullato", 6) },
            Enum.GetValues<Casazen.Core.Entities.Enums.ServiceRequestStatus>().Select(s => (s.ToString(), (int)s)));
        // SP-10 appended the customer of the public showcase after CasaZen itself; the integers of the others did not move.
        Assert.Equal(
            new[] { ("Host", 0), ("Supplier", 1), ("System", 2), ("Customer", 3) },
            Enum.GetValues<Casazen.Core.Entities.Enums.ServiceRequestActorParty>().Select(a => (a.ToString(), (int)a)));
        Assert.Equal(
            new[] { ("ShortRent", 0), ("LongRent", 1), ("Showcase", 2) },
            Enum.GetValues<Casazen.Core.Entities.Enums.ServiceRequestRentalContext>().Select(c => (c.ToString(), (int)c)));
        Assert.Equal(
            new[] { ("Host", 0), ("Showcase", 1) },
            Enum.GetValues<Casazen.Core.Entities.Enums.ServiceRequestSource>().Select(c => (c.ToString(), (int)c)));
    }

    [Fact]
    public void Sources_TheSourceOfARequestIsCasazenForAHostAndShowcaseForACustomerOfThePublicShowcase()
    {
        Assert.Equal("casazen", SupplierRequestSources.CasaZen);
        Assert.Equal("showcase", SupplierRequestSources.Showcase);
        Assert.Equal("casazen", SupplierRequestSources.Of(Casazen.Core.Entities.Enums.ServiceRequestSource.Host));
        Assert.Equal("showcase", SupplierRequestSources.Of(Casazen.Core.Entities.Enums.ServiceRequestSource.Showcase));
        Assert.Equal("NoResponse", ServiceRequestCancellationReasons.NoResponse);
    }

    private static Dictionary<string, string> ReadEntries(CultureInfo culture)
    {
        var manager = new ResourceManager(typeof(SharedResources).FullName!, typeof(SharedResources).Assembly);
        var set = manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        Assert.NotNull(set);
        return set
            .Cast<DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => entry.Value as string ?? string.Empty, StringComparer.Ordinal);
    }
}
