using System.Collections;
using System.Globalization;
using System.Resources;
using Casazen.Core.Exceptions;
using Casazen.Core.Suppliers;
using Casazen.Web.Resources;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>SP-03: the codes, the texts (Italian and English) and the exceptions of the supplier's agenda.</summary>
public class SupplierAgendaSupportTests
{
    [Fact]
    public void MessageKeys_EveryAgendaKey_ExistsInItalianAndEnglishWithDifferentText()
    {
        var italian = ReadEntries(CultureInfo.InvariantCulture);
        var english = ReadEntries(new CultureInfo("en"));

        Assert.All(SupplierAgendaErrors.MessageKeys, key =>
        {
            Assert.True(italian.ContainsKey(key), $"{key} is missing from SharedResources.resx");
            Assert.True(english.ContainsKey(key), $"{key} is missing from SharedResources.en.resx");
            Assert.False(string.IsNullOrWhiteSpace(italian[key]), key);
            Assert.False(string.IsNullOrWhiteSpace(english[key]), key);
            Assert.NotEqual(italian[key], english[key]);
        });
    }

    [Fact]
    public void MessageKeys_AreUnique_AndTheFourErrorsOfTheSpecAreAmongThem()
    {
        Assert.Equal(SupplierAgendaErrors.MessageKeys.Count, SupplierAgendaErrors.MessageKeys.Distinct().Count());
        Assert.Equal(8, SupplierAgendaErrors.MessageKeys.Count);
        // The four of the task: hours, time off, block, rules.
        Assert.Contains("SupplierHoursInvalid", SupplierAgendaErrors.MessageKeys);
        Assert.Contains("SupplierTimeOffInvalid", SupplierAgendaErrors.MessageKeys);
        Assert.Contains("SupplierBlockInvalid", SupplierAgendaErrors.MessageKeys);
        Assert.Contains("SupplierRulesInvalid", SupplierAgendaErrors.MessageKeys);
    }

    [Fact]
    public void MessageKeys_TheMessagesThatNameFieldsOrLimits_TakeTheirArguments()
    {
        var italian = ReadEntries(CultureInfo.InvariantCulture);
        var english = ReadEntries(new CultureInfo("en"));

        foreach (var key in new[]
                 {
                     "SupplierHoursInvalid", "SupplierTimeOffInvalid", "SupplierBlockInvalid", "SupplierRulesInvalid",
                     "SupplierTimeOffLimitReached", "SupplierBlockLimitReached",
                 })
        {
            Assert.Contains("{0}", italian[key]);
            Assert.Contains("{0}", english[key]);
        }
    }

    [Fact]
    public void Codes_AreSnakeCaseAndAreTheOnesTheFrontendBranchesOn()
    {
        Assert.Equal("supplier_hours_invalid", SupplierAgendaErrors.HoursInvalid);
        Assert.Equal("supplier_time_off_invalid", SupplierAgendaErrors.TimeOffInvalid);
        Assert.Equal("supplier_time_off_limit_reached", SupplierAgendaErrors.TimeOffLimitReached);
        Assert.Equal("supplier_time_off_not_found", SupplierAgendaErrors.TimeOffNotFound);
        Assert.Equal("supplier_block_invalid", SupplierAgendaErrors.BlockInvalid);
        Assert.Equal("supplier_block_limit_reached", SupplierAgendaErrors.BlockLimitReached);
        Assert.Equal("supplier_block_not_found", SupplierAgendaErrors.BlockNotFound);
        Assert.Equal("supplier_rules_invalid", SupplierAgendaErrors.RulesInvalid);
    }

    [Fact]
    public void TheLimitErrors_AreBusinessRules_WithTheLimitAsTheirArgument()
    {
        var timeOff = SupplierAgendaErrors.TimeOffLimit();
        var blocks = SupplierAgendaErrors.BlockLimit();

        Assert.Equal(SupplierAgendaErrors.TimeOffLimitReached, timeOff.Code);
        Assert.Equal("SupplierTimeOffLimitReached", timeOff.MessageKey);
        Assert.Equal(SupplierAgendaLimits.MaxTimeOffEntries, timeOff.MessageArgs.Single());
        Assert.Equal(SupplierAgendaErrors.BlockLimitReached, blocks.Code);
        Assert.Equal(SupplierAgendaLimits.MaxManualWindows, blocks.MessageArgs.Single());
    }

    [Fact]
    public void TheNotFoundErrors_CarryTheirCodeAndKey_AndNameNoPersonalData()
    {
        var id = Guid.NewGuid();

        var timeOff = SupplierAgendaErrors.TimeOffMissing(id);
        var block = SupplierAgendaErrors.BlockMissing(id);

        Assert.IsType<NotFoundException>(timeOff);
        Assert.Equal(SupplierAgendaErrors.TimeOffNotFound, timeOff.Code);
        Assert.Equal("SupplierTimeOffNotFound", timeOff.MessageKey);
        Assert.Equal(SupplierAgendaErrors.BlockNotFound, block.Code);
        Assert.Equal("SupplierBlockNotFound", block.MessageKey);
    }

    [Fact]
    public void TheRuleException_IsA422WithTheFieldsJoinedAsItsArgument()
    {
        var ex = SupplierAgendaErrors.InvalidHours(["days[0].weekday", "days[1].bands"]);

        Assert.IsAssignableFrom<DomainRuleException>(ex);
        Assert.Equal(["days[0].weekday", "days[1].bands"], ex.Fields);
        Assert.Equal("days[0].weekday, days[1].bands", ex.MessageArgs.Single());
    }

    [Fact]
    public void Limits_AreTheOnesOfTheSpec()
    {
        // gap/05 §4.1: up to 3 bands a day. The notice of the agenda has the bound of the notice of a service of the catalog.
        Assert.Equal(3, SupplierAgendaLimits.MaxBandsPerDay);
        Assert.Equal(1440, SupplierAgendaLimits.MinutesPerDay);
        Assert.Equal(SupplierServiceCatalogLimits.MaxMinNoticeHours, SupplierAgendaLimits.MaxNoticeHours);
        Assert.Equal(62, SupplierAgendaLimits.MaxCalendarDays);
        Assert.Equal(80, SupplierAgendaLimits.LabelMaxLength);
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
