using System.Globalization;
using System.Text;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The proposals of the mapping step of the activation of a channel (E09/F02/US01/T02, NF-56): a room of the channel is proposed the product of the
/// listing that has the same name once both are normalised (lower case, no accents, letters and digits only, one space between words), and nothing
/// else. No similarity, no guess: when the name is not exactly the same, or is the same for two products, or for two rooms of the channel, there is
/// no proposal and the person chooses. A room that is mapped already, and a room the channel manager cannot manage, are not proposed anything.
/// </summary>
internal static class ChannelRoomMappingProposer
{
    /// <summary>The product proposed for each room of the channel that has one, by the id of the room.</summary>
    public static IReadOnlyDictionary<string, string> Propose(IReadOnlyList<OctorateChannelRoom> channelRooms, IReadOnlyList<OctorateRoom> products)
    {
        ArgumentNullException.ThrowIfNull(channelRooms);
        ArgumentNullException.ThrowIfNull(products);

        var productsByName = products
            .Select(product => (Id: product.Id, Name: Normalize(product.Name)))
            .Where(product => product.Name.Length > 0)
            .GroupBy(product => product.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(product => product.Id).Distinct(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);

        var roomsByName = channelRooms
            .Where(room => room.Manageable && room.MappedProductId is null)
            .GroupBy(room => Normalize(room.Name), StringComparer.Ordinal)
            .Where(group => group.Key.Length > 0)
            .ToList();

        var proposals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in roomsByName)
        {
            // Two rooms of the channel with the same name (two rates of one room) cannot both be «the» room of one product.
            if (group.Count() != 1)
                continue;

            if (productsByName.TryGetValue(group.Key, out var ids) && ids.Count == 1)
                proposals[group.First().ChannelRoomId] = ids[0];
        }

        return proposals;
    }

    /// <summary>Lower case, without accents or punctuation, the words separated by one space; empty for a blank name.</summary>
    internal static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var letters = new StringBuilder(name.Length);
        foreach (var character in name.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
                continue;

            letters.Append(char.IsLetterOrDigit(character) ? character : ' ');
        }

        return string.Join(' ', letters.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
